param([string]$SampleImage)
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath (Join-Path $env:APPDATA 'Microsoft/UserSecrets/ta-backend-development/secrets.json') -Raw | ConvertFrom-Json
function Invoke-Worker([byte[]]$Bytes) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $settings.'Biometrics:WorkerPath'
    $start.Arguments = '"' + $settings.'Biometrics:DetectorModelPath' + '" "' + $settings.'Biometrics:ArcFaceModelPath' + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.BaseStream.Write($Bytes, 0, $Bytes.Length)
        $process.StandardInput.Close()
        if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Native worker timed out.' }
        if ($process.ExitCode -ne 0) { throw "Native worker failed with exit code $($process.ExitCode)." }
        $result = $output.Result | ConvertFrom-Json
        if ($result.error -eq 'face_quality_insufficient' -and $errors.Result -match 'quality: [^\r\n]+') { Write-Host $Matches[0] }
        return $result
    } finally { $process.Dispose() }
}
$invalid = Invoke-Worker ([byte[]](0, 1, 2))
if ($invalid.error -ne 'invalid_image') { throw 'Malformed image was not rejected.' }
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap(256, 256)
$stream = New-Object IO.MemoryStream
try {
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $blank = Invoke-Worker $stream.ToArray()
    if ($blank.error -ne 'exactly_one_face_required') { throw "Unexpected blank-image result: $($blank.error)." }
} finally { $bitmap.Dispose(); $stream.Dispose() }
Write-Host 'PASS: malformed input rejected; blank PNG decoded and rejected by real SCRFD inference.'
if ($SampleImage) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $SampleImage).Path)
    $face = Invoke-Worker $bytes
    if ($face.error -or $face.embedding.Count -ne 512) { throw "Sample extraction failed: $($face.error)." }
    $squared = 0.0
    foreach ($value in $face.embedding) {
        if ([double]::IsNaN($value) -or [double]::IsInfinity($value)) { throw 'Non-finite embedding.' }
        $squared += $value * $value
    }
    if ([Math]::Abs($squared - 1) -gt 0.001) { throw 'Embedding was not normalized.' }
    Write-Host 'PASS: real SCRFD detection and ArcFace extraction produced a finite normalized 512-dimensional embedding.'
}
