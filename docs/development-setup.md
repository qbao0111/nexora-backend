# Nexora Team Development Setup

**Status:** Approved internal-development onboarding
**Last updated:** 2026-08-31

This guide gets a teammate from a fresh machine to a running Nexora API + Worker. It does not provision production/staging infrastructure or resolve DEC-01–04.

## 1. Install prerequisites

- Git.
- .NET 10 SDK; the supported SDK line is pinned by [`global.json`](../global.json).
- PowerShell 7 (`pwsh`) on Windows is recommended.
- Access to the GitHub repository and the dedicated Neon `development` branch credentials.
- Optional: Node.js LTS for the separate Vite frontend.

Local PostgreSQL is not required when using Neon. Docker is needed only for the optional offline Compose workflow.

Verify the tools:

```powershell
git --version
dotnet --version
pwsh --version
```

## 2. Clone and verify the repository

```powershell
git clone https://github.com/qbao0111/nexora-backend.git
cd nexora-backend
git switch main
git pull --ff-only origin main
dotnet tool restore
dotnet restore
dotnet build --no-restore
dotnet test --no-build --no-restore
```

Do not begin feature work when the baseline build/tests fail. Ask the maintainer before changing SDK/package pins merely to fix a local-machine mismatch.

## 3. Configure secrets on each machine

Obtain development credentials from the maintainer through an approved private channel. Do not paste them into source files, `.env`, issue/PR text, screenshots or application logs.

Both API and Worker use the shared user-secrets ID `Nexora.LocalDevelopment`, so configure secrets once:

```powershell
dotnet user-secrets set "ConnectionStrings:Postgres" "NEON_DEVELOPMENT_NPGSQL_CONNECTION" --project src/Nexora.Api
dotnet user-secrets set "Ai:Provider" "Fake" --project src/Nexora.Api
```

Use only the Neon `development` branch. The guarded scripts reject non-Neon hosts for this workflow and never expose a remote reset/drop action. Fake AI is sufficient for initial FE integration and needs no AI key. For explicitly approved live Gemini testing, configure `Ai:Provider=Gemini`, `Ai:Gemini:ApiKey` and `Ai:Gemini:Model` through user-secrets. Gemini remains unapproved for production under DEC-01.

Verify presence without sharing values:

```powershell
dotnet user-secrets list --project src/Nexora.Api |
    ForEach-Object { ($_ -split ' = ', 2)[0] }
```

## 4. Start the development backend

```powershell
pwsh ./scripts/run-development.ps1
```

The command restores/builds, applies source-controlled migrations, starts API + Worker, waits for readiness and keeps both alive until the terminal is stopped.

Check:

- API readiness: `http://localhost:5088/api/v1/health`
- Swagger UI (Development only): `http://localhost:5088/swagger`
- OpenAPI: `http://localhost:5088/openapi/v1.json`
- Runtime logs: `.nexora-local/logs/`
- Frontend origins: `http://localhost:5173` or `http://localhost:3000`

Keep this terminal open while developing the frontend. Start the Vite frontend in a second terminal. Follow [frontend-integration.md](frontend-integration.md) for browser contracts.

Swagger uses the existing OpenAPI document, with Bearer authorization, required idempotency headers and raw PDF/DOCX upload inputs. It does not persist authorization across reloads or use an external schema validator. Neither UI nor JSON is exposed in Staging/Production; Testing retains JSON only. See the [Vietnamese FE setup and Swagger walkthrough](frontend-swagger-guide.vi.md) for a copy-ready checklist and resume troubleshooting.

## 5. Test payment and AI flows

Normal automated tests use deterministic Fake AI and no live network. For a browser-created fake checkout, copy its `orderId` and fulfill it without exposing the webhook secret:

```powershell
pwsh ./scripts/complete-fake-payment.ps1 -OrderId "ORDER_ID"
```

Refetch `/api/v1/me` after fulfillment. To verify the complete real Gemini development contract with synthetic data:

```powershell
pwsh ./scripts/gemini-live-smoke.ps1
```

The live smoke may consume provider quota. Never use a real candidate CV, JD or transcript.

## 6. Team branch and pull-request workflow

Use one coherent feature/phase per branch so it can be reviewed or reverted independently:

```powershell
git switch main
git pull --ff-only origin main
git switch -c feature/short-scope-name
```

Before pushing:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --no-restore
git diff --check
```

Update `project_log.md` after a completed verified slice, commit, push and create a PR. Merge only after the PR is conflict-free and required checks pass. Never commit user-secrets or `.nexora-local/` runtime data.

## 7. Common problems

| Symptom | Check |
| --- | --- |
| API never becomes ready | Inspect `.nexora-local/logs/api.stderr.log`; verify Neon connection and migrations. |
| Interview stays `starting` or report stays `completing` | Worker must be running; inspect Worker logs and AI configuration. |
| Browser CORS failure | Use exactly `http://localhost:5173` or `http://localhost:3000`; do not open HTML through `file://`. |
| Refresh returns `401` | Use `credentials: "include"`, keep API/FE on `localhost`, and confirm the refresh cookie exists. |
| Protected call returns `401` | Send the current in-memory access token in the Bearer header. |
| `IDEMPOTENCY_KEY_REQUIRED` | Generate one UUID for that mutation intent and reuse it only for retries. |
| `QUOTA_EXCEEDED` | Complete a development fake checkout, then refetch `/me`. |
| AI provider unavailable/rate-limited | Verify secret presence/model config, inspect safe logs, wait/back off; never move the API key into FE. |
| EF model drift | Pull latest `main`, rebuild, then run `pwsh ./scripts/neon-dev-db.ps1 Migrate`. |

If a problem remains, share the request ID, safe error code and relevant log timestamp—not tokens, connection strings or raw candidate content.
