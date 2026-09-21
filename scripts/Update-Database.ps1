$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/Ta.Backend/Ta.Backend.csproj'
dotnet ef database update --project $project
if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }
$secretsPath = Join-Path $env:APPDATA 'Microsoft/UserSecrets/ta-backend-development/secrets.json'
$secrets = Get-Content -LiteralPath $secretsPath -Raw | ConvertFrom-Json
$connection = $secrets.'ConnectionStrings:Migration'
$password = [regex]::Match($connection, 'Password=([0-9a-f]{64})').Groups[1].Value
if ($password.Length -ne 64) { throw 'Unexpected local migration credential format.' }
try {
    $env:PGPASSWORD = $password
    $sql = @'
GRANT SELECT, INSERT, UPDATE ON device TO ta_backend_app;
GRANT SELECT, INSERT ON attendance_event, gallery_release TO ta_backend_app;
GRANT SELECT, INSERT ON attendance_decision, audit_record TO ta_backend_app;
GRANT SELECT, INSERT, UPDATE ON account, account_session, study_program, academic_term, course,
    classroom, student, lecturer, academic_class, course_registration, class_membership,
    academic_schedule, teaching_session, session_roster, device_room_assignment,
    session_attendance, biometric_enrollment TO ta_backend_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON course_registration_item, biometric_template TO ta_backend_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ta_backend_app;
'@
    $sql | & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -X -w -q -h localhost -U ta_backend_owner -d ta_backend_dev -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw 'Runtime permission setup failed.' }
} finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
Write-Host 'Migrations applied; runtime role has only the table permissions required by the API.'
