$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath (Join-Path $env:APPDATA 'Microsoft/UserSecrets/ta-backend-development/secrets.json') -Raw | ConvertFrom-Json
$password = [regex]::Match($settings.'ConnectionStrings:Backend', 'Password=([0-9a-f]{64})').Groups[1].Value
if ($password.Length -ne 64) { throw 'Unexpected generated runtime credential format.' }
try {
    $env:PGPASSWORD = $password
    $sql = @'
SELECT current_user = 'ta_backend_app'
  AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole
  AND NOT has_schema_privilege(current_user, 'public', 'CREATE')
  AND has_table_privilege(current_user, 'device', 'SELECT')
  AND has_table_privilege(current_user, 'device', 'INSERT')
  AND has_table_privilege(current_user, 'device', 'UPDATE')
  AND has_table_privilege(current_user, 'attendance_event', 'SELECT')
  AND has_table_privilege(current_user, 'attendance_event', 'INSERT')
  AND has_table_privilege(current_user, 'gallery_release', 'SELECT')
  AND has_table_privilege(current_user, 'gallery_release', 'INSERT')
  AND NOT has_table_privilege(current_user, 'attendance_event', 'UPDATE')
  AND NOT has_table_privilege(current_user, 'attendance_event', 'DELETE')
  AND NOT has_table_privilege(current_user, 'gallery_release', 'UPDATE')
  AND NOT has_table_privilege(current_user, 'gallery_release', 'DELETE')
  AND has_table_privilege(current_user, 'account', 'SELECT')
  AND has_table_privilege(current_user, 'account', 'INSERT')
  AND has_table_privilege(current_user, 'account', 'UPDATE')
  AND has_table_privilege(current_user, 'course_registration_item', 'SELECT')
  AND has_table_privilege(current_user, 'course_registration_item', 'INSERT')
  AND has_table_privilege(current_user, 'course_registration_item', 'UPDATE')
  AND has_table_privilege(current_user, 'course_registration_item', 'DELETE')
  AND has_table_privilege(current_user, 'biometric_template', 'SELECT')
  AND has_table_privilege(current_user, 'biometric_template', 'INSERT')
  AND has_table_privilege(current_user, 'biometric_template', 'UPDATE')
  AND has_table_privilege(current_user, 'biometric_template', 'DELETE')
  AND has_table_privilege(current_user, 'session_attendance', 'SELECT')
  AND has_table_privilege(current_user, 'session_attendance', 'INSERT')
  AND has_table_privilege(current_user, 'session_attendance', 'UPDATE')
  AND has_table_privilege(current_user, 'attendance_decision', 'SELECT')
  AND has_table_privilege(current_user, 'attendance_decision', 'INSERT')
  AND NOT has_table_privilege(current_user, 'attendance_decision', 'UPDATE')
  AND NOT has_table_privilege(current_user, 'attendance_decision', 'DELETE')
  AND has_table_privilege(current_user, 'audit_record', 'SELECT')
  AND has_table_privilege(current_user, 'audit_record', 'INSERT')
  AND NOT has_table_privilege(current_user, 'audit_record', 'UPDATE')
  AND NOT has_table_privilege(current_user, 'audit_record', 'DELETE')
FROM pg_roles WHERE rolname = current_user;
'@
    $result = $sql | & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -w -q -t -A -h localhost -U ta_backend_app -d ta_backend_dev -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0 -or ($result -join '').Trim() -ne 't') { throw 'Runtime database permission verification failed.' }
} finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
$health = Invoke-RestMethod 'https://localhost:7143/health/ready'
if ($health.status -ne 'ready') { throw 'HTTPS database readiness failed.' }
Write-Host 'PASS: runtime database least-privilege grants and trusted HTTPS readiness.'
