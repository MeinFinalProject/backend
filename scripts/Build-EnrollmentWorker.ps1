param(
    [Parameter(Mandatory = $true)][string]$EdgeSource,
    [switch]$ConfigureDevelopment
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$edge = (Resolve-Path -LiteralPath $EdgeSource).Path.Replace('\', '/')
$build = Join-Path $root '.local/enrollment-worker'
cmake -S (Join-Path $root 'tools/EnrollmentWorker') -B $build -A x64 "-DEDGE_SOURCE_DIR=$edge"
if ($LASTEXITCODE -ne 0) { throw 'Enrollment worker configure failed.' }
cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw 'Enrollment worker build failed.' }
if ($ConfigureDevelopment) {
    $model = Join-Path $edge 'models/insightface/buffalo_l/w600k_r50.onnx'
    $detector = Join-Path $edge 'models/insightface/buffalo_sc/det_500m.onnx'
    if (!(Test-Path -LiteralPath $model) -or !(Test-Path -LiteralPath $detector)) { throw 'Required Edge models are missing.' }
    $settings = @{
        'Biometrics:WorkerPath' = (Join-Path $build 'Release/enrollment_worker.exe')
        'Biometrics:ArcFaceModelPath' = $model
        'Biometrics:DetectorModelPath' = $detector
        'Biometrics:ModelSha256' = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $settings | ConvertTo-Json | dotnet user-secrets set --project (Join-Path $root 'src/Ta.Backend')
    if ($LASTEXITCODE -ne 0) { throw 'Enrollment worker configuration failed.' }
}
Write-Host 'Enrollment worker ready. Edge source files were linked without modification.'
