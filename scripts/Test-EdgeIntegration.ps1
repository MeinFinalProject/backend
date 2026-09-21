[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EdgeSource)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$edge = (Resolve-Path -LiteralPath $EdgeSource).Path
$profilePath = Join-Path $edge 'edge-config.backend.local.json'
if (!(Test-Path -LiteralPath $profilePath)) { throw 'Run Initialize-EdgeDevelopment.ps1 first.' }
$profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$uri = [uri]$profile.base_url
if ($profile.mode -ne 'development' -or !$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or !$uri.IsLoopback -or
    $uri.AbsolutePath.TrimEnd('/') -ne '/api/v1' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
    throw 'Integration tests require a development profile targeting loopback HTTPS /api/v1.'
}
try {
    $null = Invoke-WebRequest -UseBasicParsing -Uri ($uri.GetLeftPart('Authority') + '/health/ready') -MaximumRedirection 0 -TimeoutSec 15
} catch { throw 'Start the backend HTTPS launch profile before running integration tests.' }
$model = if ([IO.Path]::IsPathRooted($profile.arcface_model)) { $profile.arcface_model } else { Join-Path $edge $profile.arcface_model }
$credential = if ([IO.Path]::IsPathRooted($profile.credential_file)) { $profile.credential_file } else { Join-Path $edge $profile.credential_file }
if (!(Test-Path -LiteralPath $credential)) { throw 'The development device credential is missing.' }
$hash = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
$build = Join-Path $root '.local/native-probe-build'
cmake -S (Join-Path $root 'tests/EdgeContractProbe') -B $build -A x64 "-DEDGE_SOURCE_DIR=$($edge.Replace('\', '/'))"
if ($LASTEXITCODE -ne 0) { throw 'Native integration test configure failed.' }
cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native integration test build failed.' }
$scratchRoot = [IO.Path]::GetFullPath((Join-Path $root '.local/integration-runs'))
$scratch = Join-Path $scratchRoot ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
try {
    # Only file paths and public configuration are arguments; DPAPI decryption happens inside Edge code.
    & (Join-Path $build 'Release/edge_contract_probe.exe') $credential $profile.device_id $hash $profile.base_url $scratch
    if ($LASTEXITCODE -ne 0) { throw 'Native Edge/backend integration failed.' }
} finally {
    $resolved = (Resolve-Path -LiteralPath $scratch).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $scratchRoot -or (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to remove a scratch directory outside the integration test root.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
