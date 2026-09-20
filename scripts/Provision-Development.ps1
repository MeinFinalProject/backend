[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$ModelPath)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/Ta.Backend/Ta.Backend.csproj'
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$credentialPath = Join-Path $PSScriptRoot '../.local/postgres-admin.clixml'
if (!(Test-Path -LiteralPath $credentialPath)) { & (Join-Path $PSScriptRoot 'Request-PostgresCredential.ps1') }
$credential = Import-Clixml -LiteralPath $credentialPath
$secretsPath = Join-Path $env:APPDATA 'Microsoft/UserSecrets/ta-backend-development/secrets.json'
$secrets = @{}
if (Test-Path -LiteralPath $secretsPath) {
    $existing = Get-Content -LiteralPath $secretsPath -Raw | ConvertFrom-Json
    foreach ($property in $existing.PSObject.Properties) { $secrets[$property.Name] = $property.Value }
}
function New-RandomSecret {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}
function Invoke-Database([string]$Database, [string]$Sql) {
    # SQL travels on stdin; passwords never become process arguments or tool output.
    $result = $Sql | & $psql -X -w -q -t -A -h localhost -p 5432 -U postgres -d $Database -v ON_ERROR_STOP=1 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL provisioning failed. Check the password and service; SQL output was suppressed to protect credentials.' }
    return ($result -join "`n").Trim()
}
try {
    $env:PGPASSWORD = $credential.GetNetworkCredential().Password
    foreach ($entry in @(
        @{ Role = 'ta_backend_owner'; Database = 'ta_backend_dev'; Key = 'ConnectionStrings:Migration' },
        @{ Role = 'ta_backend_app'; Database = 'ta_backend_dev'; Key = 'ConnectionStrings:Backend' },
        @{ Role = 'ta_backend_test'; Database = 'ta_backend_test'; Key = 'Tests:Postgres' }
    )) {
        $role = $entry.Role
        $exists = Invoke-Database 'postgres' "SELECT 1 FROM pg_roles WHERE rolname = '$role';"
        if (!$secrets.ContainsKey($entry.Key)) {
            if ($exists -eq '1') { throw "Role $role already exists without matching local secrets; refusing to change its password." }
            $password = New-RandomSecret
            $secrets[$entry.Key] = "Host=localhost;Port=5432;Database=$($entry.Database);Username=$role;Password=$password;Include Error Detail=false"
            # Save before creation so an interrupted run can resume without losing the generated password.
            $secrets | ConvertTo-Json | dotnet user-secrets set --project $project | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Unable to save user secrets.' }
        } else {
            $password = [regex]::Match($secrets[$entry.Key], 'Password=([0-9a-f]{64})').Groups[1].Value
            if ($password.Length -ne 64) { throw 'Unexpected generated credential format.' }
        }
        if ($exists -ne '1') {
            $null = Invoke-Database 'postgres' "CREATE ROLE $role LOGIN PASSWORD '$password' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;"
        }
    }
    foreach ($entry in @(
        @{ Database = 'ta_backend_dev'; Owner = 'ta_backend_owner' },
        @{ Database = 'ta_backend_test'; Owner = 'ta_backend_test' }
    )) {
        $database = $entry.Database
        $owner = $entry.Owner
        $actualOwner = Invoke-Database 'postgres' "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = '$database';"
        if (!$actualOwner) { $null = Invoke-Database 'postgres' "CREATE DATABASE $database OWNER $owner;" }
        elseif ($actualOwner -ne $owner) { throw "Database $database has an unexpected owner; refusing to modify it." }
        $null = Invoke-Database 'postgres' "REVOKE ALL ON DATABASE $database FROM PUBLIC; GRANT CONNECT ON DATABASE $database TO $owner;"
        $null = Invoke-Database $database 'REVOKE ALL ON SCHEMA public FROM PUBLIC;'
    }
    $null = Invoke-Database 'postgres' 'GRANT CONNECT ON DATABASE ta_backend_dev TO ta_backend_app;'
    $null = Invoke-Database 'ta_backend_dev' 'GRANT USAGE ON SCHEMA public TO ta_backend_app;'
    if (!$secrets.ContainsKey('Administration:Token')) { $secrets['Administration:Token'] = New-RandomSecret }
    $secrets['Biometrics:ModelSha256'] = (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $secrets | ConvertTo-Json | dotnet user-secrets set --project $project | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to save user secrets.' }
    # ASP.NET user secrets are plaintext, so restrict this application's secrets directory.
    $secretDirectory = Split-Path -Parent $secretsPath
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    [IO.Directory]::SetAccessControl($secretDirectory, $acl)
    Remove-Item -LiteralPath $credentialPath
    Write-Host 'Development and test databases provisioned. Credentials stored in restricted ASP.NET user secrets. Temporary administrator credential removed.'
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    $credential = $null
    $password = $null
}
