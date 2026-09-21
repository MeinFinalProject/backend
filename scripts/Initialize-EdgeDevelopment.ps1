[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EdgeSource,
    [ValidatePattern('^[a-zA-Z0-9_-]{1,64}$')][string]$DeviceId = 'edge-dev-01',
    [uri]$BaseUrl = 'https://localhost:7143/api/v1'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
# Never send a bootstrap credential to a remote host or follow an HTTP redirect.
if (!$BaseUrl.IsAbsoluteUri -or $BaseUrl.Scheme -ne 'https' -or !$BaseUrl.IsLoopback -or
    $BaseUrl.AbsolutePath.TrimEnd('/') -ne '/api/v1' -or $BaseUrl.UserInfo -or $BaseUrl.Query -or $BaseUrl.Fragment) {
    throw 'Development setup requires a loopback HTTPS URL ending in /api/v1.'
}
$api = $BaseUrl.AbsoluteUri.TrimEnd('/')
$edge = (Resolve-Path -LiteralPath $EdgeSource).Path
$profilePath = Join-Path $edge 'edge-config.backend.local.json'
$templatePath = Join-Path $edge 'edge-config.example.json'
if (!(Test-Path -LiteralPath (Join-Path $edge 'edge_app/sync/wire_format.cpp')) -or !(Test-Path -LiteralPath $templatePath)) {
    throw 'EdgeSource must point to the root of the Edge client checkout.'
}
$secrets = Get-Content -LiteralPath (Join-Path $env:APPDATA 'Microsoft/UserSecrets/ta-backend-development/secrets.json') -Raw | ConvertFrom-Json
if (!$secrets.'Administration:Token') { throw 'Run backend development provisioning first.' }
$localDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.local'))
$credentialFile = Join-Path $localDirectory "$DeviceId.dpapi"
$database = "data/backend-development/$DeviceId/edge.db"
if (Test-Path -LiteralPath $profilePath) {
    $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    if ($profile.mode -ne 'development' -or $profile.device_id -ne $DeviceId -or $profile.base_url -ne $api -or
        $profile.database -ne $database -or $profile.credential_file -ne $credentialFile) {
        throw 'Existing edge-config.backend.local.json uses different settings. Review it before rerunning setup; it has not been overwritten.'
    }
} else {
    $profile = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json
    $profile.device_id = $DeviceId
    $profile.base_url = $api
    $profile.database = $database
    $profile.credential_file = $credentialFile
}
$model = if ([IO.Path]::IsPathRooted($profile.arcface_model)) { $profile.arcface_model } else { Join-Path $edge $profile.arcface_model }
$hash = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $secrets.'Biometrics:ModelSha256') { throw 'Backend and Edge ArcFace model checksums differ.' }
try {
    $null = Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl.GetLeftPart('Authority') + '/health/ready') -MaximumRedirection 0 -TimeoutSec 15
} catch { throw 'Backend readiness or HTTPS verification failed. Start the HTTPS launch profile first.' }
New-Item -ItemType Directory -Force -Path $localDirectory | Out-Null
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
[IO.Directory]::SetAccessControl($localDirectory, $acl)
$headers = @{ Authorization = 'Bearer ' + $secrets.'Administration:Token' }
$bytes = $null
$token = $null
try {
    if (Test-Path -LiteralPath $credentialFile) {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($credentialFile), $null, 'CurrentUser')
        $token = [Text.Encoding]::UTF8.GetString($bytes)
    } else {
        $body = @{ device_id = $DeviceId; device_name = 'Local development Edge' } | ConvertTo-Json
        try {
            $response = Invoke-RestMethod -Uri "$api/admin/devices" -Method Post -Headers $headers -ContentType 'application/json' -Body $body -MaximumRedirection 0 -TimeoutSec 15
        } catch { throw 'Device registration failed. Existing devices without a local credential require explicit credential recovery; setup does not rotate credentials.' }
        $token = $response.token
        $bytes = [Text.Encoding]::UTF8.GetBytes($token)
        $protected = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, 'CurrentUser')
        [IO.File]::WriteAllBytes($credentialFile, $protected)
        $response = $null
    }
    $deviceHeaders = @{ Authorization = "Bearer $token" }
    try {
        $gallery = Invoke-RestMethod -Uri "$api/gallery" -Headers $deviceHeaders -MaximumRedirection 0 -TimeoutSec 15
    } catch {
        if (!$_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 503) { throw 'Device authentication or HTTPS verification failed.' }
        # Generate from approved active templates instead of replacing them with an empty release.
        $null = Invoke-RestMethod -Uri "$api/admin/gallery-regeneration" -Method Post -Headers $headers -MaximumRedirection 0 -TimeoutSec 15
        $gallery = Invoke-RestMethod -Uri "$api/gallery" -Headers $deviceHeaders -MaximumRedirection 0 -TimeoutSec 15
    }
    if ($gallery.model_sha256 -ne $hash) { throw 'Published gallery and Edge ArcFace model checksums differ.' }
    if (!(Test-Path -LiteralPath $profilePath)) {
        [IO.File]::WriteAllText($profilePath, ($profile | ConvertTo-Json -Depth 5) + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
    }
    Write-Host "Development device ready: $DeviceId"
    Write-Host "Edge profile: $profilePath"
    Write-Host "API: $api; gallery templates: $(@($gallery.templates).Count)"
    Write-Host 'Run edge_client.exe --config edge-config.backend.local.json from the Edge checkout. An empty gallery requires approved enrollment before recognition.'
} finally {
    if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
    $token = $null
    $headers = $null
    $deviceHeaders = $null
    $secrets = $null
}
