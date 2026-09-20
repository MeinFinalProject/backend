$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$credential = Get-Credential -UserName postgres -Message 'TA Backend: enter the local PostgreSQL administrator password. It is stored temporarily with Windows DPAPI, never in source control.'
if ($null -eq $credential) { exit 1 }
$localDirectory = Join-Path $PSScriptRoot '../.local'
New-Item -ItemType Directory -Force -Path $localDirectory | Out-Null
$credential | Export-Clixml -LiteralPath (Join-Path $localDirectory 'postgres-admin.clixml')
