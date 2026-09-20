using System.Buffers.Binary;
using Ta.Backend.Common;

namespace Ta.Backend.Features.Biometrics;

public sealed record GalleryDocument(int SchemaVersion, string GalleryVersion, string EmbeddingModel,
    string ModelSha256, int EmbeddingDimension, string EmbeddingEncoding, GalleryTemplate[] Templates)
{
    public string? Validate(string expectedHash)
    {
        if (SchemaVersion != 1 || !WireJson.Identifier(GalleryVersion)
            || EmbeddingModel != "insightface/w600k_r50" || ModelSha256 != expectedHash
            || EmbeddingDimension != 512 || EmbeddingEncoding != "f32le-base64"
            || Templates is null || Templates.Length > 10000) return "invalid_gallery_metadata";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in Templates)
        {
            if (template is null || !WireJson.Identifier(template.TemplateId) || !ids.Add(template.TemplateId)
                || !WireJson.Identifier(template.IdentityId) || template.Dimension != 512
                || template.Encoding != "f32le-base64" || template.Data is not { Length: 2732 })
                return "invalid_template";
            var bytes = new byte[2048];
            if (!Convert.TryFromBase64String(template.Data, bytes, out var length) || length != 2048
                || Convert.ToBase64String(bytes) != template.Data) return "invalid_embedding_encoding";
            double square = 0;
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4));
                if (!float.IsFinite(value)) return "non_finite_embedding";
                square += (double)value * value;
            }
            if (Math.Sqrt(square) < 1e-12) return "zero_embedding";
        }
        return null;
    }
}
public sealed record GalleryTemplate(string TemplateId, string IdentityId, int Dimension, string Encoding, string Data);
