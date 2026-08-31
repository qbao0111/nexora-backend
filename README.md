# Nexora

**Status:** **INTERNAL DEVELOPMENT ENVIRONMENT READY (NEON)**

**Current milestone:** Shared Neon development baseline verified; ready for continued internal feature work

**Specification baseline:** Approved implementation baseline
**Last updated:** 2026-08-25

Nexora is an AI-powered interview-practice application. Its journey is **CV/JD → personalised mock interview → rubric/evidence-based feedback → report → further practice**. The MVP is a coaching product, not a covert assistant for live interviews.

The backend is a .NET 10 modular monolith using ASP.NET Core 10, Entity Framework Core 10, PostgreSQL, ASP.NET Core Identity, a separate Worker, and provider-neutral AI/payment/storage adapters. The static frontend consumes REST JSON under `/api/v1`.

## Start here

1. Coding agents read [AGENTS.md](AGENTS.md), then [SPEC.md](SPEC.md).
2. Locate formal requirement IDs in [docs/SRS.md](docs/SRS.md).
3. Use the [documentation index](docs/README.md) for the owning detailed specification.
4. Review [project_log.md](project_log.md) for completed slices and verification evidence.

New teammates should follow [Team Development Setup](docs/development-setup.md). A team member with a fresh machine can use Neon and does not need to install PostgreSQL locally.

## Internal development with Neon

Nexora uses a **dedicated Neon development branch/database** for shared internal development. It must not be a staging or production database. This is an internal-development dependency only and does not resolve DEC-04 or select production infrastructure.

Prerequisites:

- .NET 10 SDK matching [global.json](global.json).
- A dedicated Neon development branch and role.
- PowerShell 7 recommended on Windows.

Neon credentials never enter source control, command examples, logs, or `appsettings*.json`. Obtain the Npgsql key/value form from the Neon development branch and keep it only in the current shell or the shared .NET user-secrets store:

```powershell
$env:NEXORA_DEV_POSTGRES = 'Host=YOUR_DEV_BRANCH.neon.tech;Database=YOUR_DEV_DATABASE;Username=YOUR_DEV_ROLE;Password=YOUR_SECRET;SSL Mode=Require'

dotnet user-secrets set "ConnectionStrings:Postgres" "$env:NEXORA_DEV_POSTGRES" --project src/Nexora.Api
```

`Nexora.Api` and `Nexora.Worker` share the `Nexora.LocalDevelopment` user-secrets ID, so the single command configures both development processes. Do not paste a real connection string into `.env.example`, chat logs, commits, or PR descriptions.

Build and apply source-controlled migrations:

```powershell
dotnet tool restore
dotnet restore
dotnet build --no-restore
dotnet test --no-build
pwsh ./scripts/neon-dev-db.ps1 Migrate
```

The Neon database script accepts only a host ending in `.neon.tech` with `SSL Mode=Require` or `VerifyFull`. It has no create, drop, or reset action. Destructive Neon branch reset remains an explicit dashboard/administration operation outside these repository scripts.

### Run API and Worker

For frontend development, run both processes from one terminal:

```powershell
pwsh ./scripts/run-development.ps1
```

The API is then available at `http://localhost:5088`, Development-only Swagger UI at `http://localhost:5088/swagger`, and OpenAPI at `http://localhost:5088/openapi/v1.json`. The development CORS allow-list accepts `http://localhost:5173` and `http://localhost:3000`. Keep that terminal open; stopping it stops both child processes. See the [Vietnamese FE setup and Swagger guide](docs/frontend-swagger-guide.vi.md) for setup, authorization, upload and troubleshooting, or the [frontend local integration handoff](docs/frontend-integration.md) for browser contracts.

Alternatively, run both from the repository root in separate terminals and configure the same URL/environment explicitly:

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:5088'
dotnet run --project src/Nexora.Api --no-launch-profile
```

```powershell
dotnet run --project src/Nexora.Worker --no-launch-profile
```

Development defaults use `FakeAiProvider`, `FakePaymentProvider`, and `LocalStorageProvider`. Both processes use the same ignored `.nexora-local/storage` path when launched from the repository root. Readiness is `/api/v1/health`; liveness is `/health/live`.

### Automated Neon smoke

The smoke script builds, migrates, launches API and Worker together, and verifies registration/login, `/me`, plans, duplicate fake webhook behavior, entitlement fulfillment, private CV/JD upload, worker analysis, active interview, official answers, idempotent report generation, dashboard history, and persistence across an API restart:

```powershell
pwsh ./scripts/local-smoke.ps1 -Neon
```

It reads the connection from `NEXORA_DEV_POSTGRES`, stops API and Worker on completion, does not print the connection string, and never resets the Neon database. Runtime logs are written under ignored `.nexora-local/logs/` without access tokens or raw request bodies.

### Optional Gemini development adapter

Fake AI remains the deterministic default. Gemini may be enabled with a development key through user-secrets:

```powershell
dotnet user-secrets set "Ai:Provider" "Gemini" --project src/Nexora.Api
dotnet user-secrets set "Ai:Gemini:ApiKey" "YOUR_DEVELOPMENT_KEY" --project src/Nexora.Api
dotnet user-secrets set "Ai:Gemini:Model" "YOUR_CONFIGURED_MODEL" --project src/Nexora.Api
```

Restart API and Worker after changing provider settings. Gemini is not selected as the DEC-01 production provider.

Run the opt-in live contract smoke only with synthetic candidate data:

```powershell
pwsh ./scripts/gemini-live-smoke.ps1
```

The script reads the Neon development connection and Gemini settings from local .NET user-secrets (or `NEXORA_DEV_POSTGRES` for the connection), then exercises the complete API + Worker journey. It is intentionally separate from `dotnet test`: normal automated tests remain deterministic and network-free. The adapter enforces a configured timeout, at most three attempts, normalized safe failures, Nexora-owned response schemas and server semantic validation. A successful development smoke does not approve Gemini for production. Do not send real CV/JD/transcript data through a free development quota; review the current [Gemini pricing/data-use terms](https://ai.google.dev/gemini-api/docs/pricing) before testing.

## Optional offline/local PostgreSQL

[compose.dev.yml](compose.dev.yml) remains available for teammates who prefer an isolated local database. PostgreSQL binds only to `127.0.0.1:54329` and uses clearly synthetic local credentials:

```powershell
pwsh ./scripts/local-db.ps1 Up
pwsh ./scripts/local-db.ps1 Migrate
pwsh ./scripts/local-smoke.ps1
```

Safe shutdown preserves the named volume: `pwsh ./scripts/local-db.ps1 Down`. `pwsh ./scripts/local-db.ps1 Reset -ResetStorage` removes only the known local Compose volume and ignored local storage root; it rejects remote hosts and database names other than `nexora_dev`.

## Fixed implementation baseline

- .NET 10 LTS, ASP.NET Core 10, Entity Framework Core 10, PostgreSQL, and ASP.NET Core Identity.
- Modular monolith: `Nexora.Api`, `Nexora.Business`, `Nexora.Data`, `Nexora.Integrations`, and `Nexora.Worker`; Presentation → Business → Data.
- Provider-neutral adapters; controllers and frontend never call AI, payment, or storage vendors directly.
- Canonical lifecycle, transactional quota ledger, ownership authorization, private files, idempotent mutations/jobs, and release gates owned by [SPEC.md](SPEC.md) and [docs/05-test-strategy.md](docs/05-test-strategy.md).

## Deferred production enablement decisions

DEC-01 through DEC-04 do **not** block internal development or fake/development adapters. They block only the corresponding real production capability:

- **DEC-01:** production AI provider/model and production AI budgets.
- **DEC-02:** production Vietnamese payment provider and refund/invoice/tax policy.
- **DEC-03:** final retention periods and approved legal/privacy text.
- **DEC-04:** production hosting vendors, domains, mail provider and infrastructure accounts.

Neon development usage does not choose the production database/hosting vendor. Local filesystem storage is not production storage. Staging deployment, production backup/restore evidence (T-10), and production readiness remain intentionally deferred.

## Next milestone

Continue internal feature work on a dedicated feature branch using the shared Neon `development` branch and deterministic fake providers. Production integration, staging/go-live evidence and DEC-01..04 remain separate work.
