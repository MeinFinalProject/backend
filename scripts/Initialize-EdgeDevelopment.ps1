[CmdletBinding()]
param([ValidatePattern('^[a-zA-Z0-9_-]{1,64}$')][string]$DeviceId = 'edge-dev-01')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$baseUrl = 'https://localhost:7143/api/v1'
$secrets = Get-Content -LiteralPath (Join-Path $env:APPDATA 'Microsoft/UserSecrets/ta-backend-development/secrets.json') -Raw | ConvertFrom-Json
$headers = @{ Authorization = 'Bearer ' + $secrets.'Administration:Token' }
$localDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.local'))
New-Item -ItemType Directory -Force -Path $localDirectory | Out-Null
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
[IO.Directory]::SetAccessControl($localDirectory, $acl)
$credentialFile = Join-Path $localDirectory "$DeviceId.dpapi"
if (Test-Path -LiteralPath $credentialFile) {
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($credentialFile), $null, 'CurrentUser')
    $token = [Text.Encoding]::UTF8.GetString($bytes)
} else {
    $body = @{ device_id = $DeviceId; device_name = 'Local development Edge' } | ConvertTo-Json
    $response = Invoke-RestMethod -Uri "$baseUrl/admin/devices" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
    $token = $response.token
    $bytes = [Text.Encoding]::UTF8.GetBytes($token)
    $protected = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, 'CurrentUser')
    [IO.File]::WriteAllBytes($credentialFile, $protected)
}
try {
    $null = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/gallery" -Headers @{ Authorization = "Bearer $token" }
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 503) { throw 'Device authentication or HTTPS verification failed.' }
    $gallery = @{
        schema_version = 1; gallery_version = 'development-empty-v1'; embedding_model = 'insightface/w600k_r50'
        model_sha256 = $secrets.'Biometrics:ModelSha256'; embedding_dimension = 512; embedding_encoding = 'f32le-base64'; templates = @()
    } | ConvertTo-Json
    $null = Invoke-RestMethod -Uri "$baseUrl/admin/gallery-releases" -Method Post -Headers $headers -ContentType 'application/json' -Body $gallery
}
[Array]::Clear($bytes, 0, $bytes.Length)
$token = $null
Write-Host "Development device ready: $DeviceId"
Write-Host "Edge-compatible Windows DPAPI credential: $credentialFile"
Write-Host "Edge base_url: $baseUrl"
