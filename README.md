# Nexora

**Status:** **REAL INTERNAL DEVELOPMENT FLOW (GEMINI)**

**Current milestone:** Real browser/API/PostgreSQL/Gemini flow is the development verification path

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

Both processes use the same ignored `.nexora-local/storage` path when launched from the repository root. Development uses Gemini for AI, `LocalStorageProvider` for private files, and a config-selected payment adapter. The default remains `FakePaymentProvider`; SePay Sandbox can be enabled explicitly for internal payment testing with no production provider decision. Readiness is `/api/v1/health`; liveness is `/health/live`.

### Gemini development configuration

Gemini is the only application AI provider. Configure its development key and model through user-secrets:

```powershell
dotnet user-secrets set "Ai:Gemini:ApiKey" "YOUR_DEVELOPMENT_KEY" --project src/Nexora.Api
dotnet user-secrets set "Ai:Gemini:Model" "YOUR_CONFIGURED_MODEL" --project src/Nexora.Api
```

Restart API and Worker after changing secrets. If AI is enabled and either value is missing, startup fails with a clear configuration error. Gemini is an internal-development integration; DEC-01 production provider/model and budget decisions remain deferred.

Use the real browser/frontend journey for CV, JD and interview validation. A normal text PDF/DOCX stays local; only suspicious extraction automatically uses one Gemini document-understanding fallback that returns extracted text and the compact resume profile together. See [frontend integration](docs/frontend-integration.md) and the Desktop guides generated for the project owner.

### SePay Sandbox payment configuration

Fake payment remains the default development adapter. To test the hosted SePay Sandbox form, explicitly set `Billing:Payment:Provider=sepay` and the SePay Sandbox secrets through user-secrets. See [SePay Sandbox payment runbook](docs/sepay-sandbox.md). Production payment stays disabled until DEC-02 is approved; this sandbox adapter is not a production payment selection.

## Optional offline/local PostgreSQL

[compose.dev.yml](compose.dev.yml) remains available for teammates who prefer an isolated local database. PostgreSQL binds only to `127.0.0.1:54329` and uses clearly synthetic local credentials:

```powershell
pwsh ./scripts/local-db.ps1 Up
pwsh ./scripts/local-db.ps1 Migrate
```

Safe shutdown preserves the named volume: `pwsh ./scripts/local-db.ps1 Down`. `pwsh ./scripts/local-db.ps1 Reset -ResetStorage` removes only the known local Compose volume and ignored local storage root; it rejects remote hosts and database names other than `nexora_dev`.

## Fixed implementation baseline

- .NET 10 LTS, ASP.NET Core 10, Entity Framework Core 10, PostgreSQL, and ASP.NET Core Identity.
- Modular monolith: `Nexora.Api`, `Nexora.Business`, `Nexora.Data`, `Nexora.Integrations`, and `Nexora.Worker`; Presentation → Business → Data.
- Provider-neutral adapters; controllers and frontend never call AI, payment, or storage vendors directly.
- Canonical lifecycle, transactional quota ledger, ownership authorization, private files, idempotent mutations/jobs, and release gates owned by [SPEC.md](SPEC.md) and [docs/05-test-strategy.md](docs/05-test-strategy.md).

## Deferred production enablement decisions

DEC-01 through DEC-04 do **not** block internal development or the explicitly documented development adapters. They block only the corresponding real production capability:

- **DEC-01:** production AI provider/model and production AI budgets.
- **DEC-02:** production Vietnamese payment provider and refund/invoice/tax policy.
- **DEC-03:** final retention periods and approved legal/privacy text.
- **DEC-04:** production hosting vendors, domains, mail provider and infrastructure accounts.

Neon development usage does not choose the production database/hosting vendor. Local filesystem storage is not production storage. Staging deployment, production backup/restore evidence (T-10), and production readiness remain intentionally deferred.

## Next milestone

Continue internal feature work on a dedicated feature branch using the shared Neon `development` branch, Gemini AI, fake payment and local storage. Production integration, staging/go-live evidence and DEC-01..04 remain separate work.
