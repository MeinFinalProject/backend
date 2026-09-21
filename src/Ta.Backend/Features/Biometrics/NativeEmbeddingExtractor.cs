using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ta.Backend.Common;

namespace Ta.Backend.Features.Biometrics;

public sealed record ExtractedFace(byte[] Embedding, double DetectionScore, double AlignmentError);
public interface IEmbeddingExtractor
{
    Task<ExtractedFace> Extract(byte[] image, CancellationToken ct);
}

// A local Windows worker links the unchanged Edge SCRFD/ArcFace implementation.
// Images travel over stdin and are never written to disk or sent to another service.
public sealed class NativeEmbeddingExtractor(IConfiguration config) : IEmbeddingExtractor, IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    public async Task<ExtractedFace> Extract(byte[] image, CancellationToken ct)
    {
        DomainException.Require(await gate.WaitAsync(0, ct), "enrollment_worker_busy", 429);
        try
        {
            var worker = config["Biometrics:WorkerPath"];
            var model = config["Biometrics:ArcFaceModelPath"];
            var detector = config["Biometrics:DetectorModelPath"];
            DomainException.Require(File.Exists(worker) && File.Exists(model) && File.Exists(detector), "enrollment_worker_not_configured", 503);
            await using (var stream = File.OpenRead(model!))
                DomainException.Require(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) == config["Biometrics:ModelSha256"], "enrollment_model_mismatch", 503);
            var start = new ProcessStartInfo(worker!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(detector!);
            start.ArgumentList.Add(model!);
            using var process = Process.Start(start) ?? throw new DomainException("enrollment_worker_unavailable", 503);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardInput.BaseStream.WriteAsync(image, timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var json = await output;
                _ = await errors; // Never log image/embedding output or native diagnostic paths.
                DomainException.Require(process.ExitCode == 0, "enrollment_inference_failed", 503);
                using var result = JsonDocument.Parse(json);
                var root = result.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.GetString();
                    throw new DomainException(code is "invalid_image" or "exactly_one_face_required" or "face_quality_insufficient" ? code : "enrollment_inference_failed",
                        code is "invalid_image" or "exactly_one_face_required" or "face_quality_insufficient" ? 400 : 503);
                }
                var values = root.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                DomainException.Require(values.Length == 512 && values.All(float.IsFinite) && Math.Abs(values.Sum(v => (double)v * v) - 1) < .001, "invalid_worker_embedding", 503);
                var bytes = new byte[2048];
                for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
                return new(bytes, root.GetProperty("detection_score").GetDouble(), root.GetProperty("alignment_error").GetDouble());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DomainException("enrollment_inference_timeout", 503); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
        }
        finally { gate.Release(); }
    }
    public void Dispose() => gate.Dispose();
}
