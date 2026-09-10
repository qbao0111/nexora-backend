# Nexora

**Status:** **REAL INTERNAL DEVELOPMENT FLOW (GEMINI DEFAULT; OPTIONAL DEEPSEEK LOCAL TEST)**

**Current milestone:** Real browser/API/PostgreSQL flow is the development verification path; Gemini remains the default text provider and DeepSeek V4 Flash is available on this local evaluation branch

**Specification baseline:** Approved implementation baseline
**Last updated:** 2026-09-09

Nexora is an AI-powered interview-practice application. Its journey is **CV/JD → personalised mock interview → rubric/evidence-based feedback → report → further practice**. The MVP is a coaching product, not a covert assistant for live interviews.

The backend is a .NET 10 modular monolith using ASP.NET Core 10, Entity Framework Core 10, PostgreSQL, ASP.NET Core Identity, a separate Worker, and provider-neutral AI/payment/storage adapters. The static frontend consumes REST JSON under `/api/v1`.

## Start here

1. Coding agents read [AGENTS.md](AGENTS.md), then [SPEC.md](SPEC.md).
2. Locate formal requirement IDs in [docs/SRS.md](docs/SRS.md).
3. Use the [documentation index](docs/README.md) for the owning detailed specification.
4. Review [project_log.md](project_log.md) for completed slices and verification evidence.

New teammates should follow [Team Development Setup](docs/development-setup.md). A team member with a fresh machine can use Neon and does not need to install PostgreSQL locally.

## Team and AI implementation workflow

Use this order before starting any implementation task:

1. Read [AGENTS.md](AGENTS.md) and [SPEC.md](SPEC.md).
2. Find the related requirement IDs and business rules in [docs/SRS.md](docs/SRS.md), then read the owning detailed document from [docs/README.md](docs/README.md).
3. Read the newest entries in [project_log.md](project_log.md) and check the current `main`, related branches and open PRs.
4. Confirm that every dependency is marked complete with a branch, commit or PR. If the dependency is not confirmed, stop before editing dependent files and sync with its owner.
5. Create a dedicated branch from the latest `main`; keep one coherent goal per PR and avoid hot files owned by another workstream.

The `project_log.md` file is the shared progress and readiness ledger. It does not replace the specification: contracts and behavior still come from `AGENTS.md`, `SPEC.md`, `docs/SRS.md` and the owning detailed document. The log tells an agent what has actually finished and what is safe to build on.

### How to write a useful `project_log.md` entry

Add one factual entry in the same PR as the completed work. Use this template:

```markdown
## YYYY-MM-DD — <capability or slice>

- Status: Completed | Blocked
- Owner: <name or team>
- Branch: `<branch-name>`
- Commit/PR: `<commit-sha>` / `#<number>`
- Scope: <what changed and what did not change>
- Contracts/traceability: <SRS, ADR, API or data-contract IDs/links>
- Files/modules: <important paths only>
- Verification: <exact tests, build, migration or manual checks and results>
- Dependencies: <completed prerequisite or `None`>
- Remaining blockers/follow-up: <explicit item or `None`>
```

Write outcomes, not plans. Keep entries specific enough for an AI agent to answer: “Is this dependency complete, where is the code, and how was it verified?” Never put API keys, connection strings, tokens, raw CV/transcript content or unverified claims in the log. If work is partial, use `Status: Blocked` and name the exact blocker; do not imply that a branch or PR is ready when it is not.

When a task depends on another teammate, the dependent agent should proceed only after the log records `Status: Completed` and a concrete commit or merged PR to sync. After syncing, record any follow-up or conflict resolution in its own entry.

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

Both processes use the same ignored `.nexora-local/storage` path when launched from the repository root. Development defaults to Gemini text AI, with optional DeepSeek V4 Flash text evaluation selected through `Ai:Provider=deepseek`; document OCR fallback remains Gemini in either mode. `Storage:Provider=local` selects the private local adapter for Development/Testing, while `Storage:Provider=r2` selects the private Cloudflare R2 adapter plus durable upload-intent/finalize flow when all `Storage:R2:*` settings are supplied. R2 direct browser PUTs use short-lived private signed URLs; the existing `POST /resumes` endpoint validates the actual object before creating a usable resume. Payment is config-selected; the default remains `FakePaymentProvider`, and SePay Sandbox can be enabled explicitly for internal payment testing with no production provider decision. Readiness is `/api/v1/health`; liveness is `/health/live`.

### Cloudflare R2 storage configuration

The R2 adapter uses an S3-compatible HTTPS endpoint and keeps objects private by default. Configure `Storage:Provider=r2` plus `Storage:R2:AccountId`, `Bucket`, `AccessKeyId`, `SecretAccessKey` and `Endpoint` through environment variables or user-secrets; startup fails closed when any required value is missing or invalid. Do not put credentials, signed URLs or raw upload tokens in source control or logs. The R2 upload-intent record is persisted in PostgreSQL, the signed PUT is short-lived, and finalize re-reads the object through `IStorageProvider` to validate actual size/signature/container/checksum. `Storage:Provider=local` remains the supported Development/Testing path; local upload is not production-safe.

### AI provider development configuration

Gemini remains the default text provider and must stay configured because the document OCR fallback is Gemini. Configure its development key and model through user-secrets:

```powershell
dotnet user-secrets set "Ai:Gemini:ApiKey" "YOUR_DEVELOPMENT_KEY" --project src/Nexora.Api
dotnet user-secrets set "Ai:Gemini:Model" "YOUR_CONFIGURED_MODEL" --project src/Nexora.Api
```

To evaluate the optional DeepSeek V4 Flash text provider locally, switch the shared user-secrets store (the API and Worker use the same `Nexora.LocalDevelopment` ID):

```powershell
dotnet user-secrets set "Ai:Provider" "deepseek" --project src/Nexora.Api
dotnet user-secrets set "Ai:DeepSeek:ApiKey" "YOUR_DEEPSEEK_KEY" --project src/Nexora.Api
dotnet user-secrets set "Ai:DeepSeek:Model" "deepseek-v4-flash" --project src/Nexora.Api
```

Restart API and Worker after changing secrets. If the selected provider or the Gemini OCR configuration is incomplete, startup fails with a clear configuration error. DeepSeek is a local/development evaluation adapter only; DEC-01 production provider/model and budget decisions remain deferred.

Use the real browser/frontend journey for CV, JD and interview validation. A normal text PDF/DOCX stays local; only suspicious extraction automatically uses one Gemini document-understanding fallback that returns extracted text and the compact resume profile together. See [frontend integration](docs/frontend-integration.md) and the Desktop guides generated for the project owner.

### SePay Sandbox payment configuration

Fake payment remains the default development adapter. To test the hosted SePay Sandbox form, explicitly switch only your local user-secrets to SePay:

```powershell
dotnet user-secrets set "Billing:Payment:Provider" "sepay" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:Environment" "Sandbox" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:MerchantId" "YOUR_SANDBOX_MERCHANT_ID" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:SecretKey" "YOUR_SANDBOX_SECRET_KEY" --project src/Nexora.Api
```

For browser return pages, configure all three callback URLs together. They must be public HTTPS frontend URLs on the same origin; localhost/HTTP are rejected:

```powershell
dotnet user-secrets set "Billing:Sepay:SuccessUrl" "https://YOUR-PUBLIC-FRONTEND/payment/success" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:ErrorUrl" "https://YOUR-PUBLIC-FRONTEND/payment/error" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:CancelUrl" "https://YOUR-PUBLIC-FRONTEND/payment/cancel" --project src/Nexora.Api
```

Configure the backend IPN separately in the SePay dashboard as `https://<ngrok-domain>/api/v1/webhooks/payments/sepay`. See [SePay Sandbox payment runbook](docs/sepay-sandbox.md). Production payment stays disabled until DEC-02 is approved; this sandbox adapter is not a production payment selection.

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

Continue internal feature work on a dedicated feature branch using the shared Neon `development` branch, Gemini by default (or the optional DeepSeek local adapter), fake payment and local storage. Production integration, staging/go-live evidence and DEC-01..04 remain separate work.
