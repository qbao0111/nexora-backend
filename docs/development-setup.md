# Nexora Team Development Setup

**Status:** Approved internal-development onboarding
**Last updated:** 2026-09-01

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
dotnet user-secrets set "Ai:Gemini:ApiKey" "GEMINI_DEVELOPMENT_KEY" --project src/Nexora.Api
dotnet user-secrets set "Ai:Gemini:Model" "GEMINI_MODEL_ID" --project src/Nexora.Api
```

Use only the Neon `development` branch. The guarded scripts reject non-Neon hosts for this workflow and never expose a remote reset/drop action. If the Neon branch selector says `production`, replace the secret with the connection from `development` before creating any test users. Gemini remains required for the document OCR fallback; the API and Worker fail clearly at startup when the Gemini secrets are missing. Text AI may use the default Gemini adapter or the optional local DeepSeek adapter. Neither is approved for production under DEC-01.

Verify presence without sharing values:

```powershell
dotnet user-secrets list --project src/Nexora.Api |
    ForEach-Object { ($_ -split ' = ', 2)[0] }
```

## 4. Start the development backend

```powershell
pwsh ./scripts/run-development.ps1
```

The command restores/builds, applies source-controlled migrations, starts API + Worker, waits for readiness and keeps both alive until the terminal is stopped. It also sets one absolute shared storage root for both processes, so uploads are available to the Worker regardless of the launch directory.

Check:

- API readiness: `http://localhost:5088/api/v1/health`
- Swagger UI (Development only): `http://localhost:5088/swagger`
- OpenAPI: `http://localhost:5088/openapi/v1.json`
- Runtime logs: `.nexora-local/logs/`
- Frontend origins: `http://localhost:5173` or `http://localhost:3000`

Keep this terminal open while developing the frontend. Start the Vite frontend in a second terminal. Follow [frontend-integration.md](frontend-integration.md) for browser contracts.

Swagger uses the existing OpenAPI document, with Bearer authorization, required idempotency headers and raw PDF/DOCX upload inputs. It does not persist authorization across reloads or use an external schema validator. Neither UI nor JSON is exposed in Staging/Production; Testing retains JSON only. See the [Vietnamese FE setup and Swagger walkthrough](frontend-swagger-guide.vi.md) for a copy-ready checklist and resume troubleshooting.

Resume extraction uses the real PDF/DOCX adapter. Text-based PDFs and DOCX files stay local; suspicious or image-only documents automatically enter the Gemini document fallback and expose `ocr_fallback` while processing.

## 5. Test payment and AI flows

Automated tests that protect critical invariants replace the AI adapter inside the test project and never call the network. For a browser-created fake checkout, copy its `orderId` and fulfill it without exposing the webhook secret:

```powershell
pwsh ./scripts/complete-fake-payment.ps1 -OrderId "ORDER_ID"
```

Refetch `/api/v1/me` after fulfillment. The owner validates the complete Gemini journey manually with the real browser/frontend and real CV/JD files; do not put provider secrets in the browser.

For hosted payment sandbox testing, keep FakePayment as the default and explicitly switch a machine to SePay only through user-secrets:

```powershell
dotnet user-secrets set "Billing:Payment:Provider" "sepay" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:Environment" "Sandbox" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:MerchantId" "YOUR_SANDBOX_MERCHANT_ID" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:SecretKey" "YOUR_SANDBOX_SECRET_KEY" --project src/Nexora.Api
```

For frontend browser return pages, configure all three public HTTPS URLs through user-secrets:

```powershell
dotnet user-secrets set "Billing:Sepay:SuccessUrl" "https://YOUR-PUBLIC-FRONTEND/payment/success" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:ErrorUrl" "https://YOUR-PUBLIC-FRONTEND/payment/error" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:CancelUrl" "https://YOUR-PUBLIC-FRONTEND/payment/cancel" --project src/Nexora.Api
```

Localhost/HTTP callback URLs are rejected intentionally. Use a Vercel preview URL or a public frontend tunnel. SePay IPN is configured separately in the merchant dashboard; for the backend IPN only, run `ngrok http 5088` and use `https://<ngrok-domain>/api/v1/webhooks/payments/sepay`. Do not confuse the backend IPN URL with the frontend callback URLs. See [sepay-sandbox.md](sepay-sandbox.md).

## 6. Seed thư viện tình huống

Nexora quản lý nội dung tình huống phỏng vấn hoàn toàn từ backend thông qua thư viện tình huống. Frontend không sở hữu hoặc hardcode nội dung tình huống mà phải tích hợp trực tiếp qua API `GET /api/v1/scenarios`.

Để hỗ trợ môi trường Development và Staging có ngay dữ liệu tình huống chuẩn xác, thực tế cho việc kiểm thử và tích hợp frontend, repository cung cấp script seed:

```powershell
pwsh ./scripts/seed-scenarios.ps1 `
  -ApiBaseUrl "http://localhost:5088" `
  -AccessToken "YOUR_ADMIN_ACCESS_TOKEN"
```

Lưu ý:
- **Mục đích bootstrap**: Script này chỉ dùng để khởi tạo dữ liệu cho Development/Staging; nội dung trên Production phải được quản lý chính thức qua Admin Portal/Admin API.
- **Yêu cầu quyền Admin**: Script gọi API quản trị chuẩn (`/api/v1/admin/scenarios`), do đó bắt buộc phải truyền token của tài khoản có role `Admin`.
- **An toàn khi chạy lặp lại (Idempotent)**: Mặc định script sẽ kiểm tra danh sách tình huống hiện có theo `slug` và bỏ qua (`skip`) những tình huống đã tồn tại, không bao giờ tạo trùng lặp.
- **Tùy chọn cập nhật (`-UpdateExisting`)**: Khi truyền switch `-UpdateExisting`, script sẽ so sánh và cập nhật các trường dữ liệu của tình huống qua `PATCH /api/v1/admin/scenarios/{id}`, đồng thời bảo đảm tình huống ở trạng thái xuất bản (`published`).
- **Xác thực public API**: Sau khi hoàn thành seed, script tự động gọi `GET /api/v1/scenarios?pageSize=50` để xác thực toàn bộ 12 tình huống mẫu đã hiển thị công khai trên thư viện người dùng.

## 7. Team branch and pull-request workflow

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

## 8. Staging deployment (Render Free)

For deployed testing and frontend team integration without running a local backend, see [`docs/render-staging.md`](render-staging.md). Staging runs on Render Free (`https://<render-host>/api/v1`) using a co-located Docker container for API and Worker with private R2 object storage; the container filesystem is not a product-storage dependency.

## 9. Common problems

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
