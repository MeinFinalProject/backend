# TA Backend

Backend for a walk-through student attendance system, integrated with the [Windows Edge client](https://github.com/MeinFinalProject/edge-client).

Built with ASP.NET Core 10, Entity Framework Core 10, and PostgreSQL. Implemented features:

- Device registration, bearer authentication, credential rotation, and access revocation.
- Biometric gallery publication and distribution with ETag support.
- Transactional attendance ingestion with idempotent event acknowledgements.
- Human accounts, student registration/approval, lecturer and administrator roles, revocable sessions, and auditing.
- Programs, terms, courses, classrooms, classes, advisors, KRS review, and temporal class membership.
- Recurring schedules, actual teaching sessions, replacements, attendance windows, and room changes.
- Academic event evaluation, scoped manual corrections, lateness, and attendance percentages.
- Twelve-sample biometric enrollment, native Edge-compatible embedding extraction, approval, deactivation, and gallery regeneration.

## Prerequisites

- .NET SDK 10.0.302, as specified in `global.json`.
- PostgreSQL 18 on `localhost:5432` for the supplied development scripts.
- A trusted ASP.NET Core HTTPS development certificate.
- The Edge client's `w600k_r50.onnx` model file to configure the gallery model checksum.

The setup scripts target Windows and the standard PostgreSQL installation directory.

## Development setup

Run from the repository root:

```powershell
dotnet tool restore
dotnet restore --locked-mode
powershell -ExecutionPolicy Bypass -File scripts/Provision-Development.ps1 -ModelPath 'C:\path\to\w600k_r50.onnx'
powershell -ExecutionPolicy Bypass -File scripts/Update-Database.ps1
```

Provisioning prompts locally for the PostgreSQL administrator password, creates the development and test databases, and stores generated credentials in ASP.NET user secrets. Migrations use a schema-owner role; the API uses a restricted application role. Run setup and development commands under the same Windows account.

| Database | Role | Purpose |
| --- | --- | --- |
| `ta_backend_dev` | `ta_backend_owner` | Schema ownership and migrations |
| `ta_backend_dev` | `ta_backend_app` | API runtime access |
| `ta_backend_test` | `ta_backend_test` | Integration tests |

Start the API:

```powershell
dotnet run --project src/Ta.Backend --launch-profile https
```

| URL | Purpose |
| --- | --- |
| `https://localhost:7143/swagger` | Interactive API documentation |
| `https://localhost:7143/openapi/v1.json` | OpenAPI specification |
| `https://localhost:7143/api/v1` | API base URL |
| `https://localhost:7143/health/ready` | Database readiness |
| `https://localhost:7143/health/live` | Application liveness |

Swagger UI and the OpenAPI endpoint are enabled only in `Development`. Use Swagger's **Authorize** button to enter the appropriate device or administration token. Both use HTTP bearer authentication with opaque tokens, not JWTs. Credentials are not persisted by Swagger UI between page loads.

Create the first human administrator with the local bootstrap credential, without copying it into a terminal command:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/New-Administrator.ps1
```

The script prompts for the administrator email, password, and display name. Subsequent logins use `POST /api/v1/auth/login`; enter the returned `access_token` under **Human** in Swagger. Human sessions expire after eight hours and are revoked by logout, password changes, or account disablement. Five failed login attempts lock an account for 15 minutes. Passwords use ASP.NET Core's `PasswordHasher`; only hashes of session and device tokens are stored.

For native biometric enrollment, install the Visual C++ build tools, Windows SDK, and CMake, then build against the same Edge checkout and models:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-EnrollmentWorker.ps1 -EdgeSource 'C:\path\to\edge-client' -ConfigureDevelopment
```

The worker uses Windows GPU inference and requires a compatible Direct3D/DirectML GPU. It links the Edge SCRFD and ArcFace source files without modifying them. Model files and generated binaries remain outside version control.

## Configuration

Development secrets use the ID `ta-backend-development`. The setup script restricts access to that application's user-secrets directory. Do not commit credentials or connection strings.

| Configuration key | Purpose |
| --- | --- |
| `ConnectionStrings:Backend` | Application database connection |
| `ConnectionStrings:Migration` | Migration database connection |
| `Tests:Postgres` | Integration-test database connection |
| `Administration:Token` | Bootstrap administration credential |
| `Biometrics:ModelSha256` | Lowercase SHA-256 of the Edge ArcFace model |
| `Biometrics:WorkerPath` | Absolute path to `enrollment_worker.exe` |
| `Biometrics:ArcFaceModelPath` | Absolute path to `w600k_r50.onnx` |
| `Biometrics:DetectorModelPath` | Absolute path to `det_500m.onnx` |

Environment variables use `__` in place of `:`. Production configuration should come from the deployment environment's secret store, with appropriate HTTPS certificates and allowed hostnames.

## API

Application endpoints use the `/api/v1` URL prefix. Health checks remain unversioned. The JSON `schema_version` describes the Edge payload format independently of the URL version.

| Method | Path | Credential |
| --- | --- | --- |
| GET | `/api/v1/admin/devices` | Administration |
| POST | `/api/v1/admin/devices` | Administration |
| POST | `/api/v1/admin/devices/{deviceId}/rotate-credential` | Administration |
| PUT | `/api/v1/admin/devices/{deviceId}/status` | Administration |
| POST | `/api/v1/admin/gallery-releases` | Administration |
| GET | `/api/v1/gallery` | Device |
| POST | `/api/v1/attendance-events/batch` | Device |

Human workflows also use `/api/v1`:

| Route family | Purpose | Access |
| --- | --- | --- |
| `/auth` | Registration options, registration, login, profile, password, logout | Public entry points; account operations require Human |
| `/admin/accounts` | Staff creation, account approval/status, profile management | Administrator |
| `/academic` | Catalogs, students, lecturers, advisors, own profile/classes | Catalog reads for human actors; administration restricted |
| `/course-registrations` | KRS draft, submission, corrections, approval, withdrawal | Own student; assigned advisor; administrator |
| `/teaching` | Recurring schedules, session generation, actual sessions | Assigned instructor; administrator; scoped student reads |
| `/attendance` | Session rosters, class summaries, student history, manual corrections | Own student; authorized instructor; administrator |
| `/biometric-enrollments` | Consent, image samples, submission, review, revocation | Own student; administrator review |
| `/admin/devices/{deviceId}/classroom` | Effective-now classroom assignment, preserving history | Administrator |
| `/admin/devices/{deviceId}/activity` | Latest received observation and assignment history | Administrator |
| `/admin/gallery-regeneration` | Publish a new snapshot from active approved templates | Administrator |
| `/admin/audit-records` | Read paginated audit records | Administrator |

JSON properties use `snake_case`; query parameters use their names shown in Swagger (for example `classId`, `studentId`). Send ISO 8601 timestamps with an offset; persistence uses UTC. Recurring timetable dates and times use `Asia/Jakarta`. Catalog/account lists accept a one-based `page` (100 records); event/audit lists accept a zero-based `offset` (100 records). Teaching session reads accept `classId`, `from`, and `until`, with a 500-session limit.

Swagger documents the routes and request schemas. Device credentials are returned once on registration or rotation; only their hashes are stored in the database.

Attendance batches return `accepted`, `duplicate`, or `rejected` for each event. Replaying the same event is safe, including concurrent retries. Reusing an event ID with different content is rejected. Acknowledgements are sent only after the transaction commits. Invalid individual events receive their own rejection; malformed batch envelopes return `400`.

Gallery releases are immutable. Devices can send `If-None-Match` with the previous ETag to receive `304` when unchanged. A gallery contains at most 10,000 templates, each with 512 float32 little-endian values encoded as base64 and matching the configured model checksum. An empty release revokes all templates.

## Academic workflow and rules

1. An administrator creates programs, terms, lecturer accounts, courses, classrooms, and classes. The default minimum attendance is 85% per term.
2. A student registers and waits for account approval. An administrator assigns the student's academic advisor.
3. The student saves and submits KRS. Only the assigned advisor or an administrator may approve it or request corrections. Approval starts class membership at the approval time; withdrawal closes the interval. Reapproval never backdates membership.
4. An instructor creates recurring schedules and explicitly generates actual sessions, or creates individual sessions. Only actual sessions are used for attendance. Replacement sessions reference a cancelled original. A room change on an individual session does not alter the recurring schedule; a permanent change creates a superseding schedule.
5. An administrator assigns each Edge device to a physical classroom. Assignment changes take effect immediately and retain the previous time interval.
6. Events are stored before their academic outcome is acknowledged. `accepted` means the raw observation was committed, not that attendance was awarded. A separate decision records unknown identity, no room assignment, out-of-window detection, missing enrollment, valid attendance, or repetition.

The occurrence timestamp determines classroom assignment, session window, and enrollment eligibility. A later KRS approval cannot make an earlier observation eligible. Approval must predate the observation and session start. Session boundaries are `[start - early_minutes, min(start + checkin_minutes, end))`; an arrival strictly later than `start + late_minutes` is late. The earliest eligible automatic observation wins even when offline events arrive out of order. Raw events and their original decisions remain available for audit.

Session rules and cancellation become immutable when the early check-in window opens. Memberships and device placement cannot be backdated through the API. Session lecturer/course snapshots preserve historical teaching assignments and course labels. A class cannot change its course or term; a term with started sessions permits only activation/deactivation. No timetable change silently rewrites historical attendance.

Manual attendance requires an authorized session instructor or administrator, a reason, and the current `revision` (`0` when no record exists). Corrections preserve an audit entry with previous and new values. Subsequent Edge events cannot overwrite a manual correction. Session updates also require their current `revision`; stale writes return `409`.

Reports include ended, non-cancelled actual sessions for which the student belonged to the roster. Missing attendance counts as `absent`. Both `present` and `late` count as attended; `excused` sessions are excluded from the percentage denominator. With no counted sessions, the percentage and below-minimum indicator are `null`. Cancelled and future sessions do not affect the percentage. There are no hour conversions, 38-hour limits, or disciplinary calculations.

## Biometric enrollment

A student starts an enrollment after accepting `research-v1` consent. Upload each JPEG/PNG to `/biometric-enrollments/{id}/samples` as `{ "pose": "frontal", "image_base64": "..." }`. Inputs must be at most 5 MiB, 112–2048 pixels per side, and upright; clients should normalize image orientation before upload.

Submission requires exactly 12 samples with at least two each labelled `frontal`, `left`, `right`, `up`, and `down`. Identical uploaded files are rejected. Orientations are capture instructions declared by the client, not automatically measured head poses. An administrator must verify the student's identity and record a review note before approving publication. Because photos are not retained, identity verification requires supervised capture or a separate institutional verification process.

The native worker decodes the image with Windows Imaging Component and adapts RGB to the NV12 BT.709 limited-range frame used by Edge. Detection, five-landmark alignment, GPU preprocessing, ArcFace inference, and embedding normalization reuse the Edge implementation. Validation requires one SCRFD face, a face box of at least 80 pixels per side, and alignment RMSE at most 8 output pixels. SCRFD retains Edge's default detection threshold of 0.50. These are prototype input checks, not a final biometric calibration or enrollment liveness test.

Images pass through memory/stdin and are not retained. The API verifies the ArcFace model checksum, limits inference to one worker at a time, and terminates workers after 60 seconds. Student/admin read responses contain sample metadata, never embeddings. Approved embeddings are stored in PostgreSQL and distributed only to authenticated Edge devices. Database backups and deployment storage still need appropriate access controls and encryption.

Approval replaces the student's previous approved enrollment and atomically publishes the resulting gallery. Revocation, template deactivation, and account disablement remove templates from subsequent gallery releases. Existing immutable releases remain as historical records; revocation is not physical erasure, and an offline Edge cannot receive it until synchronization resumes. Academic eligibility still uses historical membership and occurrence time.

## Edge integration

With the API running, provision a development device:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Initialize-EdgeDevelopment.ps1
```

This creates `edge-dev-01`, saves its credential to `.local/edge-dev-01.dpapi`, and publishes an empty gallery if no release exists. Configure these fields in the Edge client's configuration:

```json
{
  "device_id": "edge-dev-01",
  "base_url": "https://localhost:7143/api/v1",
  "credential_file": "C:/path/to/TA_backend/.local/edge-dev-01.dpapi"
}
```

The client appends `/gallery` and `/attendance-events/batch` to `base_url`, so API versioning requires only this configuration change. Run Edge under the Windows account that provisioned the DPAPI credential. An empty gallery must be replaced with enrolled templates before recognition can identify students.

The wire contract follows [`edge-client` commit `051d6d4`](https://github.com/MeinFinalProject/edge-client/tree/051d6d43e321da1200cd5c1504ec451c74176299), particularly `edge_app/sync/wire_format.cpp`. Edge pauses synchronization on `401`/`403`, dead-letters batches on `400`/`422`, and retries transient failures. The backend preserves these response semantics.

## Project structure

```text
src/Ta.Backend/
  Program.cs             Application composition and middleware
  Common/                API routes, OpenAPI configuration, JSON conventions
  Features/
    Identity/            Human accounts, sessions, registration, approval
    AcademicManagement/  Catalogs, KRS, memberships, schedules, sessions
    Devices/             Credentials and device management
    Biometrics/          Native enrollment, templates, gallery distribution
    Attendance/          Raw ingestion, academic decisions, corrections, reports
    Audit/               Append-only operation and correction records
  Persistence/           EF Core mappings and migrations
tests/
  Ta.Backend.Tests/      HTTP integration and validation tests
  EdgeContractProbe/     Optional native Edge compatibility test
scripts/                 Local provisioning and development helpers
tools/EnrollmentWorker/  Thin image adapter linking the Edge vision code
```

The application is a modular monolith with one deployable host. Features own their endpoints and contracts; a shared EF Core context manages persistence. Domain tables use singular snake_case names and descriptive entity-prefixed columns.

Attendance stores immutable observations. Identity IDs and gallery versions remain external references so valid offline observations are retained even when academic eligibility fails. Separate decision, session-roster, and attendance tables hold academic results. Unique constraints enforce one attendance per student/session and one decision per raw event. Foreign keys restrict deletion of referenced academic records.

Academic mutations and event evaluation share a short PostgreSQL transaction lock to serialize temporal changes for this laboratory-scale deployment. Native inference runs before that lock. Gallery publication has its own transaction lock. The runtime role cannot update/delete raw events, gallery releases, decisions, or audit records. This deliberately simple concurrency model should be revisited if deployment grows beyond the laboratory workload.

## Tests and migrations

```powershell
dotnet test Ta.Backend.slnx
```

Tests use PostgreSQL via `Tests:Postgres` and create a temporary schema in `ta_backend_test`. They cover role boundaries, account/session revocation, KRS review, offline temporal eligibility, manual overrides, reporting, schedule conflicts, gallery lifecycle, concurrent retries, and transaction rollback. CI runs the same suite against PostgreSQL 18. Enrollment workflow tests use a clearly identified extraction test double; they do not establish real recognition accuracy.

Run native inference separately with the configured worker and a suitable single-face image:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Test-EnrollmentWorker.ps1 -SampleImage 'C:\path\to\sample.png'
```

This checks malformed-input rejection, real SCRFD rejection of a blank image, and a finite normalized 512-dimensional ArcFace result. Without `-SampleImage`, only the rejection checks run. A public test fixture is available from [scikit-image's astronaut dataset](https://scikit-image.org/docs/stable/api/skimage.data.html#skimage.data.astronaut); testing that image is not an enrollment or field-accuracy experiment.

For schema changes:

```powershell
dotnet ef migrations add MigrationName --project src/Ta.Backend --output-dir Persistence/Migrations
powershell -ExecutionPolicy Bypass -File scripts/Update-Database.ps1
```

Commit migrations alongside the model changes. The API does not apply migrations at startup.

Each EF Core migration has an ordered timestamp, a `.cs` file with schema operations, and a `.Designer.cs` metadata file. The shared `BackendDbContextModelSnapshot.cs` records the current model for generating future migrations. Keep these files in Git; they are part of the database source, not build output.

The optional native probe requires an Edge checkout, CMake, and the Visual C++ build tools:

```powershell
cmake -S tests/EdgeContractProbe -B .local/native-probe-build -A x64 -DEDGE_SOURCE_DIR=C:/path/to/edge-client
cmake --build .local/native-probe-build --config Release
$settings = Get-Content "$env:APPDATA/Microsoft/UserSecrets/ta-backend-development/secrets.json" -Raw | ConvertFrom-Json
& .local/native-probe-build/Release/edge_contract_probe.exe .local/edge-dev-01.dpapi edge-dev-01 $settings.'Biometrics:ModelSha256'
```

It uses the Edge client's HTTP transport and serializers to check HTTPS authentication, gallery parsing, conditional requests, and attendance acknowledgements. Each run inserts one synthetic observation with identity `backend-contract-probe`.

## Scope and remaining experiments

- Build administrator, lecturer, and student frontend workflows against the versioned API.
- Collect consented, genuinely varied enrollment samples and test recognition with the actual laboratory camera and students.
- Evaluate latency, recognition thresholds, liveness, and offline recovery under field conditions. Native compatibility and transaction tests do not establish biometric accuracy.
- Edge currently sends verification metadata, not photographic evidence, spoof notifications, a heartbeat, or gallery-install acknowledgements. Device activity reports the latest received observation; it does not claim live connectivity.
- Email delivery/recovery, institutional SSO, discipline, tuition, and unrelated university administration are outside this prototype.
