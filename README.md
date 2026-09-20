# TA Backend

Backend for a walk-through student attendance system, integrated with the [Windows Edge client](https://github.com/MeinFinalProject/edge-client).

Built with ASP.NET Core 10, Entity Framework Core 10, and PostgreSQL. Implemented features:

- Device registration, bearer authentication, credential rotation, and access revocation.
- Biometric gallery publication and distribution with ETag support.
- Transactional attendance ingestion with idempotent event acknowledgements.

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

## Configuration

Development secrets use the ID `ta-backend-development`. The setup script restricts access to that application's user-secrets directory. Do not commit credentials or connection strings.

| Configuration key | Purpose |
| --- | --- |
| `ConnectionStrings:Backend` | Application database connection |
| `ConnectionStrings:Migration` | Migration database connection |
| `Tests:Postgres` | Integration-test database connection |
| `Administration:Token` | Bootstrap administration credential |
| `Biometrics:ModelSha256` | Lowercase SHA-256 of the Edge ArcFace model |

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

Swagger provides request and response schemas for each operation. Device credentials are returned once on registration or rotation; only their hashes are stored in the database.

Attendance batches return `accepted`, `duplicate`, or `rejected` for each event. Replaying the same event is safe, including concurrent retries. Reusing an event ID with different content is rejected. Acknowledgements are sent only after the transaction commits. Invalid individual events receive their own rejection; malformed batch envelopes return `400`.

Gallery releases are immutable. Devices can send `If-None-Match` with the previous ETag to receive `304` when unchanged. A gallery contains at most 10,000 templates, each with 512 float32 little-endian values encoded as base64 and matching the configured model checksum. An empty release revokes all templates.

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
    Devices/             Credentials and device management
    Biometrics/          Gallery validation and distribution
    Attendance/          Event validation and ingestion
  Persistence/           EF Core mappings and migrations
tests/
  Ta.Backend.Tests/      HTTP integration and validation tests
  EdgeContractProbe/     Optional native Edge compatibility test
scripts/                 Local provisioning and development helpers
```

The application is a modular monolith with one deployable host. Features own their endpoints and contracts; a shared EF Core context manages persistence. Domain tables use singular snake_case names and descriptive entity-prefixed columns.

Attendance stores immutable observations. Identity IDs and gallery versions remain external references so events captured offline can be ingested before academic enrollment exists. Device relationships are enforced by a foreign key. Published galleries are stored as immutable distribution documents; future enrollment records can produce the same contract.

## Tests and migrations

```powershell
dotnet test Ta.Backend.slnx
```

Tests use PostgreSQL via `Tests:Postgres` and create a temporary schema in `ta_backend_test`. They cover authentication, credential rotation, gallery distribution, concurrent retries, validation, and transaction rollback. CI runs the same suite against PostgreSQL 18.

For schema changes:

```powershell
dotnet ef migrations add MigrationName --project src/Ta.Backend --output-dir Persistence/Migrations
powershell -ExecutionPolicy Bypass -File scripts/Update-Database.ps1
```

Commit migrations alongside the model changes. The API does not apply migrations at startup.

The optional native probe requires an Edge checkout, CMake, and the Visual C++ build tools:

```powershell
cmake -S tests/EdgeContractProbe -B .local/native-probe-build -A x64 -DEDGE_SOURCE_DIR=C:/path/to/edge-client
cmake --build .local/native-probe-build --config Release
$settings = Get-Content "$env:APPDATA/Microsoft/UserSecrets/ta-backend-development/secrets.json" -Raw | ConvertFrom-Json
& .local/native-probe-build/Release/edge_contract_probe.exe .local/edge-dev-01.dpapi edge-dev-01 $settings.'Biometrics:ModelSha256'
```

It uses the Edge client's HTTP transport and serializers to check HTTPS authentication, gallery parsing, conditional requests, and attendance acknowledgements. Each run inserts one synthetic observation with identity `backend-contract-probe`.

## Planned modules

- Identity: human accounts, administrative permissions, and auditing.
- Academic Management: students, schedules, and attendance decisions.
- Biometrics: enrollment and gallery generation.
