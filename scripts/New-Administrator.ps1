param([string]$BaseUrl = 'https://localhost:7143/api/v1')
$ErrorActionPreference = 'Stop'
if (![Uri]::IsWellFormedUriString($BaseUrl, [UriKind]::Absolute) -or ([Uri]$BaseUrl).Scheme -ne 'https') { throw 'HTTPS is required.' }
$credential = Get-Credential -Message 'Create an administrator: email and a password of at least 12 characters.'
if ($null -eq $credential) { throw 'Administrator creation cancelled.' }
$name = Read-Host 'Administrator display name'
$settings = Get-Content -LiteralPath (Join-Path $env:APPDATA 'Microsoft/UserSecrets/ta-backend-development/secrets.json') -Raw | ConvertFrom-Json
$body = @{ email = $credential.UserName; password = $credential.GetNetworkCredential().Password; name = $name; role = 'administrator' } | ConvertTo-Json
try {
    $response = Invoke-RestMethod -Method Post -Uri ($BaseUrl.TrimEnd('/') + '/admin/accounts') -ContentType 'application/json' -Body $body `
        -Headers @{ Authorization = 'Bearer ' + $settings.'Administration:Token' }
    Write-Host "Administrator created: $($response.account_id). Sign in through POST /auth/login."
} finally { $body = $null; $credential = $null; $settings = $null }
