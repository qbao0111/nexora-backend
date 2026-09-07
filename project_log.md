# Nexora Backend Implementation Log

This log records completed implementation milestones and verification evidence. It must never contain credentials or other secrets.

## 2026-08-25 — Baseline and repository inspection completed

- Confirmed the active backend workspace is `E:\NexoraBackend`.
- Read `AGENTS.md`, `SPEC.md`, the Phase 0/1 task, and relevant SRS/API/test/security/ADR/data/delivery sections.
- Confirmed the approved modular-monolith projects and dependency direction already exist on .NET 10.
- Confirmed Phase 1 scope: Identity/auth/profile, PostgreSQL EF migration, canonical API errors/correlation/health, owner/admin authorization primitives, and development storage abstraction.
- Found no frozen-specification contradiction. Google OAuth requires a clean boundary in Phase 1; live provider configuration remains deferred.
- Next: complete repository engineering files and implementation foundation.

## 2026-08-25 — Engineering and Phase 1 foundation compiled

- Added repository-wide .NET 10 SDK/package/analyzer/editor/ignore configuration and secret-safe environment examples.
- Preserved the approved project dependency direction: API/Worker compose Business, Data and Integrations; Data and Integrations depend only on Business.
- Added provider-neutral Business contracts for authentication, external identity, owner authorization and private storage.
- Added PostgreSQL EF Core/Identity persistence with `ApplicationUser : IdentityUser<Guid>`, profile metadata and hashed refresh-token records.
- Added short-lived JWT access tokens, rotating refresh-token cookie sessions, per-session/all-session revocation and security-stamp access-token invalidation.
- Added `/api/v1` auth/profile controllers, canonical error envelopes, request IDs, liveness/readiness health checks, owner/admin policy primitives and development-only local storage.
- Replaced vulnerable transitive OpenAPI/SQLite packages with audited newer versions selected by restore metadata.
- Evidence: `dotnet build Nexora.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- Next: create the initial migration and deterministic Phase 0/1 tests.

## 2026-08-25 — Initial migration and Phase 0/1 tests completed

- Created source-controlled PostgreSQL migration `InitialIdentityFoundation` for Identity, profiles and concurrency-protected hashed refresh tokens.
- Added unit tests for owner isolation (including no implicit Admin bypass) and development storage privacy/path traversal behavior.
- Added deterministic API integration tests using an in-memory SQLite test host only; normal application startup still uses PostgreSQL and never calls `EnsureCreated()`.
- Covered register/login/current-user, secure refresh cookie attributes, refresh rotation/replay rejection, logout-all access/refresh revocation, T-01 guest mutation, canonical validation errors, correlation IDs, health, Testing-only OpenAPI and PostgreSQL migration discovery.
- Evidence: build succeeded with 0 warnings/errors; 5 unit and 7 integration tests passed.
- Next: apply the migration to the supplied PostgreSQL environment, start the API and verify readiness health.

## 2026-08-25 — Phase 0/1 verification completed

- Applied `InitialIdentityFoundation` successfully to the supplied Neon PostgreSQL database using process-only secret configuration.
- Started the API with the Production environment and supplied PostgreSQL configuration; liveness and database readiness both returned HTTP 200.
- Confirmed OpenAPI is unavailable in Production (HTTP 404) and remains available only in Development/Testing.
- Final verification passed: restore; build with 0 warnings/errors; 5 unit + 7 integration tests; format verification; no known vulnerable direct/transitive packages; no supplied database credential fragments in repository files.
- Removed tracked `bin/` and `obj/` artifacts from the repository index; `.gitignore` now keeps regenerated build outputs out of source control.
- Confirmed frozen documentation was not changed and no specification conflict was found.
- Status: **READY FOR PHASE 2**.

## 2026-08-25 — Phase 2 billing domain and fake payment path completed

- Added the server-owned plan catalogue model and public `GET /api/v1/plans`.
- Seeded the approved current VND catalogue from the supplied pricing screen: Free `0`/no expiry/1 interview, Basic `49,000`/3 days/3 interviews, Weekly `189,000`/14 days/20 interviews and Pro `599,000`/90 days/unlimited interviews.
- Added authenticated idempotent checkout creation with server-side price snapshots, user-owned order history and current entitlement/usage in `GET /api/v1/me`.
- Added `IPaymentProvider` and deterministic `FakePaymentProvider` with HMAC signature and timestamp verification; no production provider was selected.
- Added payment events, subscriptions, entitlements, immutable usage events, idempotency records and outbox records through the source-controlled `Phase2BillingEntitlement` migration.
- Added transactional quota `reserve`, `consume`, `void` and audited `adjustment` flows; PostgreSQL paths lock entitlement/order projections with `SELECT ... FOR UPDATE` under `ReadCommitted`.
- Next: finish Phase 2 integration evidence and repository quality gates.

## 2026-08-25 — Phase 2 integration evidence completed

- Added T-03 coverage proving two concurrent reservations with one remaining quota create at most one reservation, followed by idempotent consume, adjustment and void ledger transitions.
- Added T-04 coverage proving duplicate verified fake payment delivery creates one payment event, one subscription and one entitlement.
- Added T-05 coverage proving an invalid webhook signature returns `401` and leaves the pending order unchanged.
- Added forged client-price rejection, checkout idempotency replay/conflict, server catalogue, current entitlement and Phase 2 migration discovery checks.
- Evidence before final gates: build succeeded with 0 warnings/errors; 5 unit and 10 integration tests passed.
- Next: run restore/build/test, format, dependency/secret scans, migration script verification, then commit and open the Phase 2 pull request.

## 2026-08-25 — Phase 2 final verification completed

- Final restore and build passed with 0 warnings/errors; 5 unit and 10 integration tests passed.
- Formatting verification passed for every changed C# and generated migration/model file.
- EF Core reported no pending model changes; idempotent fresh and `InitialIdentityFoundation` upgrade scripts generated successfully.
- NuGet reported no known vulnerable direct or transitive packages across all projects.
- Secret-pattern scan found only documented `.env.example` placeholders and deterministic test passwords; no connection string, API key or live secret was added.
- Status: **READY FOR PHASE 2 PULL REQUEST**.

## 2026-08-25 — Phase 3 private CV/JD boundary completed

- Added short-lived development upload intents backed by private storage; final database records contain only storage keys, checksum, MIME and size, never public file URLs.
- Added bounded PDF/DOCX extension, MIME, size and server-side signature validation before a final `stored_file`/`resume` record can exist.
- Added resumable extraction and CV–JD analysis outbox jobs with owner isolation, input version snapshots and prompt/model/schema audit versions.
- Added deterministic test document extraction and structured test AI coverage; the optional Gemini adapter is configuration-driven, sends its API key only in a request header and is blocked in Production pending DEC-01.
- Evidence: T-06 rejects an executable renamed to PDF with no final storage/database record; the valid private resume/JD path completes a versioned analysis.

## 2026-08-25 — Phase 3 interview lifecycle and quota boundary completed

- Added the canonical interview session/question/official-answer persistence model and `draft`-compatible `starting → active → completing → completed`, `starting → failed` state transitions with optimistic versioning.
- Added `IAiProvider.GenerateStructuredAsync<T>`, deterministic test-project AI coverage, bounded untrusted input, explicit response schemas and server semantic validation.
- Made interview start one transaction for quota reserve + `starting` session + outbox; worker success atomically persists the first question + consume + active, while pre-activation failure atomically voids + fails.
- Evidence: T-07 proves both success and deterministic timeout boundaries; refresh retains the persisted question order and official answers.

## 2026-08-25 — Phase 3 report and dashboard milestone completed

- Added idempotent official-answer evaluation, persisted follow-up questions, weighted server-side rubric scoring and one immutable evidence/strengths/gaps/action-plan report with the required coaching disclaimer.
- Added free report retry while `completing`; a terminal report failure grants exactly one audited quota adjustment before a later retry can complete.
- Added owner-scoped interview/report reads and database-backed dashboard history with current billing/quota state.
- Added source-controlled `Phase3CoreAiPractice` PostgreSQL migration for CV/JD, analysis, interview and report tables.
- Evidence: T-08 proves answer/question/report durability after refresh, dashboard persistence and cross-owner `404`; report failure/retry coverage proves one credit and one final report.
- Next: run full repository quality gates, scan dependencies/secrets, verify migration scripts, then open the Phase 3 pull request.

## 2026-08-25 — Phase 3 final verification completed

- Final restore and build passed with 0 warnings/errors; 5 unit and 15 integration tests passed.
- Formatting and whitespace verification passed for the solution and working tree.
- EF Core reported no pending model changes; the full idempotent PostgreSQL migration script generated successfully through `Phase3CoreAiPractice`.
- NuGet reported no known vulnerable direct or transitive packages across all projects.
- Secret-pattern scan found only the documented `Password=replace-me` placeholder in `.env.example`; no connection string, API key or live secret was added.
- Status: **READY FOR PHASE 3 PULL REQUEST**.

## 2026-08-25 — Phase 4 vendor-neutral API hardening completed

- Added configuration-driven fixed-window rate limits for authentication, refresh, upload intent, checkout, AI job creation and official-answer submission using the ASP.NET Core rate-limiting middleware.
- Added canonical `429 RATE_LIMITED` responses with `Retry-After`; limits partition by IP, authenticated user or interview session as appropriate.
- Added independent AI, payment-creation and upload-intent feature gates while preserving authentication precedence and keeping payment webhooks available for reconciliation.
- Added deterministic integration evidence for rate-limit rejection and feature-gate behavior.
- Production provider, infrastructure and legal choices remain unchanged and deferred under DEC-01 through DEC-04.
- Next: implement core data export and an audited asynchronous account deletion/anonymization path for FR-AUTH-04/T-09.

## 2026-08-25 — Phase 4 privacy and recovery-test foundation completed

- Added owner-scoped core-data export with an explicit allowlist that excludes private storage keys, checksums, credentials and provider secrets.
- Added idempotent asynchronous deletion requests that immediately revoke access/refresh tokens, retry with a bounded policy and retain an auditable request state.
- The deletion worker removes private storage objects and personal CV/JD/analysis/interview/profile/auth data, then anonymizes the Identity account while preserving billing/usage audit records.
- Added source-controlled `Phase4PrivacyHardening` migration and T-09 integration evidence for export, session revocation, database/storage deletion and account anonymization.
- Added guarded PostgreSQL backup/isolated-restore/API-read rehearsal and k6 staging CRUD baseline artifacts. T-10 and load evidence remain pending an approved isolated staging target.
- Production provider, infrastructure, retention periods and legal approvals remain blocked by DEC-01 through DEC-04; no deferred decision was invented.

## 2026-08-25 — Phase 4 vendor-neutral final verification completed

- Final restore and build passed with 0 warnings/errors; 5 unit and 19 integration tests passed, including rate-limit/feature-gate coverage and T-09 privacy deletion.
- Formatting passed for every Phase 4 source file without rewriting the already-applied initial migration.
- EF Core reported no pending model changes and generated the full idempotent PostgreSQL migration script successfully through `Phase4PrivacyHardening`.
- NuGet reported no known vulnerable direct or transitive packages. Secret-pattern review found only documented placeholders and deterministic test passwords; no connection string, API key or live secret was added.
- T-10 restore rehearsal and k6 load execution are explicitly pending because this workstation has no approved isolated PostgreSQL target and does not have `pg_dump`, `pg_restore` or k6 installed.
- Status: **READY FOR PHASE 4 VENDOR-NEUTRAL HARDENING PULL REQUEST**; the complete production Phase 4 exit remains blocked by DEC-01 through DEC-04 and staging/go-live evidence.

## 2026-08-25 — Phase 4 observability and security-review milestone completed

- Added structured request completion/5xx logs with request ID, actor ID, route, status and duration without bodies, query strings, credentials or sensitive candidate content.
- Added structured job/deletion/payment outcome logs with stable resource/correlation IDs, queue lag/duration and safe exception type only.
- Added vendor-neutral operational health thresholds for stale queue jobs, long-pending payments and recent terminal job/deletion failures at `/api/v1/health/operations`.
- Added NFR-OBS-01 integration evidence proving stale queue work degrades the operational health signal while normal database readiness remains separate.
- Completed the Phase 4 source security review and made internal-development AI, Fake Payment and Local Storage/upload features fail closed in Production until their deferred decisions approve replacements.
- Remaining production gates are unchanged: DEC-01–04, browser auth/CSRF/CORS/TLS E2E, T-10 restore, staging load/DAST, external alert delivery and deployment.

## 2026-08-25 — Phase 4 observability/security final verification completed

- Final restore/build passed with 0 warnings/errors; 7 unit and 20 integration tests passed.
- Phase-scoped formatting and whitespace checks passed; EF Core reported no pending model changes and generated the idempotent migration script successfully.
- NuGet reported no known vulnerable direct or transitive packages. Secret review found only documented placeholders and deterministic test credentials.
- No production provider, hosting vendor, legal text, retention period or infrastructure account was selected.
- Status: **READY FOR PHASE 4 OBSERVABILITY/SECURITY PULL REQUEST**; full production exit still requires the unchanged staging, DEC-01–04 and go-live evidence.

## 2026-08-25 — Internal-development tooling prepared

- Added a dedicated Neon development-database workflow using secret-only connection configuration, TLS validation, source-controlled migration/model checks and no scripted remote reset/drop action.
- Kept `compose.dev.yml` and guarded local database/reset scripts as an optional offline path; PostgreSQL is loopback-only with a named volume and synthetic development credentials.
- Added an automated API + Worker smoke covering authentication, plans, duplicate fake payment delivery, entitlement, private CV/JD, worker analysis, the full interview/report journey, dashboard history and API-restart persistence.
- Replaced fixed one-second worker polling with configurable bounded adaptive backoff, immediate reset after work, cancellation-aware waits and deterministic progression/reset/cap tests.
- Added atomic outbox claiming with stale-claim recovery so concurrent workers do not normally process the same practice job.
- Verification so far: restore/build passed with 0 warnings/errors; 10 unit and 20 integration tests passed; formatting, EF model drift, dependency vulnerability and secret-pattern checks passed.
- Pending evidence: run the end-to-end smoke against the dedicated Neon development branch after `NEXORA_DEV_POSTGRES` is supplied through secret configuration. No production/staging readiness or T-10 claim is made.
- Status: **INTERNAL DEVELOPMENT ENVIRONMENT NOT YET READY — Neon development connection is required for the final PostgreSQL smoke.**

## 2026-08-25 — Neon internal-development verification completed

- Created the non-expiring Neon `development` branch under the existing `nexora-backend` project, forked from `production`; the production branch was not modified by the readiness workflow.
- Stored the development connection only in the shared local .NET user-secrets store, cleared the browser clipboard/runtime value and removed the one-time temporary transfer file; no live connection string entered the repository or task output.
- Applied all source-controlled migrations through `Phase4PrivacyHardening` to the development branch and confirmed EF Core has no pending model changes.
- Ran API and Worker simultaneously against Neon PostgreSQL with the internal AI adapter, Fake Payment and Local Storage; readiness became healthy.
- End-to-end smoke passed registration/login, `/me`, plans, duplicate verified fake webhook, entitlement, private CV/JD upload, worker extraction/analysis, interview activation, both official answers, completion/report, dashboard and persistence after API restart.
- PostgreSQL-specific transactional quota and atomic outbox-claim paths were exercised by the live flow; one order/report was observed through API projections and no duplicate processing surfaced.
- No staging/production deployment, provider decision, production backup/restore (T-10) or production-readiness claim was made.
- Status: **INTERNAL DEVELOPMENT ENVIRONMENT READY (NEON)**.

## 2026-08-25 — Gemini development contract verified

- Hardened `GeminiAiProvider` with configuration-driven timeout and bounded retry, provider-neutral failure categories, safe response handling and defensive structured-output parsing; no provider body or API key reaches Business/API errors.
- Added purpose-specific instructions for the exact Nexora rubric, grounded evidence and concise bounded report content while preserving server-side schema and semantic validation.
- Added 4 deterministic unit tests for request/schema isolation, bounded rate-limit retry, authentication-error normalization and invalid structured-response retry/failure. Automated tests remain network-free by replacing the AI adapter inside the test project.
- Added an explicit opt-in Gemini development check that uses local user-secrets, Neon development PostgreSQL and non-production test inputs only.
- Live evidence passed the complete flow: auth, duplicate fake payment webhook, entitlement, private CV/JD, Gemini analysis, interview questions, both official-answer evaluations, report, dashboard and API-restart persistence.
- Final restore/build passed with 0 warnings/errors; 14 unit and 20 integration tests passed. Formatting, EF model drift, dependency vulnerability and secret-pattern checks passed.
- Gemini remains a development adapter only. DEC-01 production provider/model and budgets are unchanged and deferred.
- Status: **GEMINI DEVELOPMENT CONTRACT READY FOR LOCAL FRONTEND INTEGRATION**.

## 2026-08-25 — Local frontend contract and teammate onboarding completed

- Added an exact development CORS allow-list for Vite on `localhost:5173`/`127.0.0.1:5173`; the runtime policy supports credentialed auth headers/cookies while unlisted origins receive no allow-origin response.
- Added one-command Neon development startup for API + Worker with guarded migrations, readiness wait, safe logs and shared local user-secrets; stopping the command stops both child processes.
- Added a signed fake-payment fulfillment helper so frontend developers can turn their own pending development order into an entitlement without exposing the webhook secret in browser code.
- Added the frontend integration handoff for auth refresh, in-memory access tokens, response/error envelopes, idempotency, private upload, polling, canonical interview states and delivery slices.
- Added fresh-machine teammate onboarding covering prerequisites, clone/build/test, per-machine secret setup, runtime checks, branch/PR workflow and troubleshooting. No local PostgreSQL installation is required for the Neon workflow.
- Live evidence: API readiness and OpenAPI returned `200`; allowed Vite preflight returned `204` with exact origin/credentials; an untrusted origin was denied; register → pending checkout → signed fake payment → Basic entitlement with 3 available interviews passed against Neon.
- Final restore/build passed with 0 warnings/errors; 14 unit and 21 integration tests passed. Formatting, PowerShell parsing, Markdown links, EF model drift, dependency vulnerability and secret-pattern checks passed.
- Status: **BACKEND READY FOR NEW FRONTEND IMPLEMENTATION ON LOCAL DEVELOPMENT CONTRACT**.

## 2026-08-31 — Development Swagger and Vietnamese FE onboarding completed

- Added Swagger UI only in Development, reusing the existing built-in OpenAPI document without introducing a second schema generator. Testing retains JSON only; Staging/Production expose neither UI nor JSON.
- Documented Bearer security from endpoint authorization metadata, the six existing idempotent mutation headers and raw PDF/DOCX upload bodies. No controller/business behavior, database schema or production provider decision changed.
- Disabled Swagger authorization persistence and external schema validation. Added deterministic environment/asset/security/header/upload metadata tests supporting FR-AUTH-02, NFR-SEC-01/02 and the existing API contract.
- Added a Vietnamese FE setup/Swagger walkthrough covering shared per-machine user-secrets, Gemini, API + Worker startup, auth, resume/JD/analysis, fake payment/interview, CORS and exact 409 troubleshooting. An identical standalone copy was delivered on the user's Desktop.
- Final restore/build passed with 0 warnings/errors; 14 unit and 27 integration tests passed. Scoped formatting, whitespace and startup-script syntax checks passed; guide copies match.
- NuGet reported no known vulnerable direct/transitive packages. Secret-pattern scan and changed-file review found only placeholders, existing synthetic test credentials and secret-setting names; no live secret was added.
- Browser smoke on an isolated local API verified the loaded OpenAPI UI, Bearer control/header, canonical 401 for an invalid synthetic token, required idempotency input and PDF/DOCX file picker. This smoke used no live database or provider and does not claim a new Neon or production E2E run.
- Status: **READY FOR DEVELOPMENT SWAGGER PULL REQUEST**.

## 2026-08-31 — FE guide aligned with Gemini development configuration

- Updated the Vietnamese FE/Swagger onboarding guide for the Development path: Gemini API key/model from user-secrets and troubleshooting for model/auth failures.
- Clarified that automated invariant tests use a test-project AI double, payment remains fake for development, and document behavior is validated through the real extraction path.
- Updated the repository guide and its standalone Desktop copy; no connection string, API key or other secret was written to either document.

## 2026-09-01 — Real PDF/DOCX extraction and development storage alignment

- Replaced the Development document extractor registration with a real PDF/DOCX text extractor using PdfPig and Open XML; image-only/scanned PDFs were initially reported as an OCR boundary.
- Added deterministic PDF and DOCX extraction tests and made the practice integration fixture a valid text PDF; the extracted content was asserted before analysis.
- Updated the Development runner to pass one absolute `.nexora-local/storage` path to both API and Worker, preventing relative-working-directory upload/extraction failures.
- Updated FE onboarding to explain real extraction and to stop when the Neon branch selector is `production`; Development secrets must use the dedicated Neon `development` branch.
- Verification: restore/build passed with 0 warnings/errors; 14 unit and 29 integration tests passed. Live Gemini/Neon verification remains opt-in and must target the development branch only.

## 2026-09-01 — Development one-call resume analysis shortcut

- Added the Development-only `POST /api/v1/dev/resume-analysis` helper for the FE debug flow: choose one PDF/DOCX and enter JD text; the backend handles upload, real extraction, JD creation and analysis queueing.
- Kept the production contract unchanged. The helper requires Bearer auth and `Idempotency-Key`, returns the generated resume/JD/analysis projections and remains hidden outside Development.
- Added request fingerprinting so retrying the same key and payload returns the existing analysis instead of creating duplicates. No manual file-size, `resumeId` or `jobDescriptionId` input is needed for this shortcut.
- Verification: restore/build passed with 0 warnings/errors; 14 unit and 31 integration tests passed, including the shortcut, idempotent retry and non-Development 404 guard. NuGet reported no vulnerable direct/transitive packages; no live secret was added.

## 2026-09-01 — PDF text NUL sanitization hotfix

- Sanitized U+0000 characters emitted by some PDF text layers before persisting `ExtractedText`; PostgreSQL rejects NUL in UTF-8 text and previously surfaced as `RESUME_EXTRACTION_FAILED`.
- Normalized text is now validated after sanitization, so a document containing only unsupported text still fails safely.
- Added a regression test using a PDF text stream containing a NUL character.

## 2026-09-01 — Document extraction V2 and compact resume context

- Kept `IDocumentExtractor` backward-compatible and added a detailed extraction result with page/character/word metrics, printable/replacement/control ratios, average usable characters per page, repeated-line ratio, bounded quality score/category and warning codes.
- Preserved PdfPig content-order extraction as the PDF fast path; added a bounded word-position line/column heuristic for suspicious or likely multi-column pages. Open XML extraction now walks body block order and flattens table rows/cells.
- Added deterministic normalization for line endings, whitespace, adjacent duplicates, repeated page boundaries, isolated page numbers and decorative separators. Suspicious/failed extraction is never silently accepted and reports that OCR may be required; OCR remains intentionally deferred with no new dependency.
- Added versioned compact `ResumeProfile` persistence and `IResumeContextBuilder`. Raw normalized CV text is sent only for the one-time profile parse; analysis, interview, answer and report requests use bounded task-specific profile context.
- Added synthetic PDF/DOCX extraction, quality, layout-fallback, Unicode, table, malformed/empty, cancellation, normalization and token-size diagnostics; extended practice coverage to verify profile caching metadata.
- Added `DocumentExtractionV2Profile` migration. No production provider, storage, OCR or secret configuration changed.

## 2026-09-01 — Document extraction V2 fixture completion

- Added synthetic Unicode-mapped PDF fixtures for Vietnamese-only and mixed Vietnamese/English text, plus an image-only PDF fixture to exercise the OCR boundary without invoking OCR.
- Final V2 test evidence: 14 unit tests and 44 integration tests passed; no package or secret changes were introduced.

## 2026-09-01 — Real internal Gemini flow and automatic document fallback

- Removed the application `FakeAiProvider`, fake-AI runtime configuration, obsolete local smoke/Gemini smoke scripts, synthetic document helpers/fixtures and the retired k6 CRUD artifact. A deterministic AI double remains only inside the integration-test project; `FakePaymentProvider` remains the sole intentionally fake runtime provider.
- Kept Document Extraction V2 unchanged at its boundaries: PdfPig content-order fast path, bounded layout reconstruction, Open XML paragraph/table order, normalization and deterministic quality metrics. Suspicious/failed local extraction now moves the public resume state through `ocr_fallback` and invokes one Gemini document-understanding request with the original bytes; the validated text and compact profile are persisted together.
- Added startup validation for required Gemini model/key configuration, model-aware profile cache identity, safe `RESUME_EXTRACTION_FAILED`/Vietnamese failure projection, owner-authorized `GET /api/v1/resumes/{id}`, a controller convention that leaves the development shortcut unmapped outside Development/Testing, and Development-aware refresh-cookie SameSite/Secure plus Origin checks for credentialed browser auth.
- Updated the API contract, README, development setup and frontend handoff for the real browser flow. Created uncommitted owner guides outside the repository: `C:\Users\THIS PC\Desktop\Nexora-BE-API-Test-Guide.md` and `C:\Users\THIS PC\Desktop\Nexora-FE-Integration-Guide.md`.
- Verification on this branch: restore and build passed with 0 warnings/errors; 42 retained unit/integration tests passed; EF model-drift check and package vulnerability scan passed; no OCR/native/Python package was added, no migration was created, and no live secret was committed. `dotnet format --verify-no-changes` still reports pre-existing line-ending/IDE findings in three unchanged API files and the InitialIdentityFoundation migration; changed files were formatted separately.

## 2026-09-01 — STAR behavioral interview coaching

- Added first-class STAR coaching to the existing interview answer evaluation JSON without creating a parallel interview flow or new database columns.
- Behavioral answers now expose structured `star` data with applicability, Situation/Task/Action/Result component scores, missing elements, strengths and coaching tips; non-STAR technical answers keep `star.applicable=false`.
- STAR overall score is computed server-side from validated components using 20/20/35/25 weighting. Follow-up generation receives weak/missing STAR signals and report responses include a deterministic optional `starSummary` when applicable answers exist.
- Bumped only the interview rubric/schema version to `interview-rubric-star-v2` / `phase3-star-v2`; ResumeProfile, extraction, quota, FakePayment and the interview state machine remain unchanged.
- Verification: restore/build passed with 0 warnings/errors; 43 retained unit/integration tests passed; EF Core reported no pending model changes. NuGet reported no vulnerable direct/transitive packages and secret-pattern review found only documented placeholders/development secret names, with no live secret committed.

## 2026-09-01 — MoMo sandbox payment adapter

- Added a provider-neutral MoMo sandbox payment adapter behind `IPaymentProvider` while preserving `FakePaymentProvider` as the default/fallback-free explicit development payment adapter.
- Checkout creation now commits a stable local order/reference before the external provider call, then stores the returned hosted checkout URL; retrying the same idempotency key reuses the same order/reference.
- Added owner-scoped checkout status and refresh endpoints so the frontend can poll payment state and manually reconcile delayed sandbox IPN by querying MoMo.
- MoMo IPN verification validates the signed JSON payload, local order reference, amount and VND currency before fulfillment. Duplicate valid deliveries stay idempotent and return `204 No Content`.
- Documented MoMo sandbox setup in `docs/momo-sandbox.md` and updated the frontend integration payment flow. DEC-02 remains unresolved and production payment remains fail-closed.
- Verification: restore/build passed with 0 warnings/errors; 47 retained unit/integration tests passed; EF Core reported no pending model changes; NuGet reported no vulnerable direct/transitive packages. Scoped formatting passed for changed files; full-repo format remains blocked by the pre-existing `InitialIdentityFoundation` migration formatting issue that must not be rewritten casually.

## 2026-09-02 — VNPAY Sandbox supersedes active MoMo development payment

- Replaced the active hosted payment provider path with VNPAY Sandbox PAY 2.1.0 behind the existing `IPaymentProvider` boundary. `FakePaymentProvider` remains the deterministic default for normal automated development/integration tests.
- Removed active MoMo runtime registration, source tests and sandbox runbook. Historical MoMo payment records and project log entries remain historical and are not rewritten.
- VNPAY checkout builds a signed sandbox payment URL locally with deterministic `nx{OrderId:N}` transaction references, VND amount ×100 conversion, GMT+7 timestamps and HMAC-SHA512 signatures.
- Added VNPAY GET IPN handling with provider-protocol JSON responses, checksum/TmnCode/reference/amount validation, duplicate handling and the shared PaymentEvent → Subscription → Entitlement fulfillment path. Checkout refresh uses VNPAY QueryDr reconciliation.
- DEC-02 remains unresolved; this is internal sandbox support only and does not enable production VNPAY.

## 2026-09-02 — VNPAY protocol correctness patch

- Separated PAY/IPN checksum data from transport query construction and added the official fixed HMAC-SHA512 regression vector.
- QueryDr now validates correlation before response-code handling, fails closed with `PAYMENT_PROVIDER_QUERY_FAILED` for protocol errors, and distinguishes pending (`00/01`) from terminal failed statuses.
- Added provider-neutral `IsFinal` and `BillingValues.Failed`; verified failed callbacks close orders without granting entitlements while duplicate callbacks remain idempotent.
- Tightened VNPAY `TmnCode` startup validation and added independent unit/integration coverage for signatures, failed/pending flows and QueryDr errors. No migration or package was added.

## 2026-09-02 — VNPAY 2.1 checksum compatibility follow-up

- Aligned PAY/IPN HMACSHA512 canonicalization with the current VNPAY 2.1.0 online migration guidance: sorted non-empty key/value pairs use application/x-www-form-urlencoded-compatible encoding before signing.
- Updated the PAY regression vector and independent test helpers; QueryDr remains pipe-signed and unchanged.

## 2026-09-02 — SePay Sandbox gateway replaces active VNPAY adapter

- Replaced the active VNPAY Sandbox runtime with a BCL-only SePay Payment Gateway Sandbox adapter; `FakePaymentProvider` remains the default deterministic test provider and DEC-02 stays unresolved.
- Checkout responses now expose an ordered POST action with SePay fields and a Base64 HMAC-SHA256 signature; checkout creation stays local and existing order/idempotency, PaymentEvent and entitlement paths are reused.
- Added SePay `ORDER_PAID`/`TRANSACTION_VOID` IPN handling with `X-Secret-Key`, sandbox REST Basic-auth reconciliation, terminal failed-payment behavior and focused mocked unit/integration coverage. No migration, package or credential was added; live Sandbox checkout/IPN verification remains manual.

## 2026-09-02 — SePay PaymentMethod validation correction

- Fixed the inverted SePay payment-method allowlist validation and added regression coverage for allowed and rejected values.

## 2026-09-02 — SePay checkout return configuration hardening

- SePay `PaymentMethod` now rejects surrounding whitespace instead of validating a trimmed copy and later submitting the untrimmed value.
- SePay return URLs now validate as a coherent public HTTPS callback triplet.
- Callback fields/signature and frontend source-of-truth behavior are covered by regression tests.
- The previously successful live Sandbox payment round-trip remains the latest manual evidence; this patch itself does not perform another external payment.

## 2026-09-06 — Realistic Vietnamese scenario library seed pass

- Added 12 Vietnamese scenario-library seed records in `scripts/data/scenarios.vi.json`.
- 4 banking / 4 ecommerce / 4 logistics distributed across 3 easy, 6 medium, and 3 hard dilemmas with standardized single-competency tags.
- Added PowerShell seeder `scripts/seed-scenarios.ps1` utilizing the canonical Admin API (`/api/v1/admin/scenarios`) and runtime category resolution.
- Seeding is strictly idempotent by scenario slug; existing slugs are skipped by default and safely updated with `-UpdateExisting`.
- Frontend remains strictly API-driven via `GET /api/v1/scenarios` with no hardcoded content; updated integration guidelines for empty/loading/error states.
- No schema migration or EF model changes introduced.

## 2026-09-06 — Identity, default user onboarding, and admin account management

- Added canonical `RoleNames.User = "User"` and `RoleNames.Admin = "Admin"` with seeded deterministic IDs (`50000000-0000-0000-0000-000000000001` and `50000000-0000-0000-0000-000000000002`).
- Added `ApplicationUser.IsActive` (boolean NOT NULL, default true).
- Created single migration `20260906101017_IdentityAdminFoundation` seeding the roles and backfilling existing users to the `User` role (`asp_net_user_roles`).
- Implemented default user onboarding: registration automatically assigns `User` role, claims `User` in JWT, provisions 100-year Free Plan entitlement with 1 mock interview quota and subscription without external payment/orders.
- Implemented active paid coexistence: active paid entitlements take precedence over `"free"` in `/api/v1/me`, `FeatureEntitlementService`, and `AdminService`.
- Account status enforcement: inactive accounts (`IsActive == false`) fail login, refresh, and immediate token validation via `JwtBearerEvents.OnTokenValidated`.
- Added authenticated password change: `POST /api/v1/me/password` validates current password, enforces 10-128 character policy, revokes active refresh tokens, and rotates security stamp.
- Added Admin account management endpoints:
  - `GET /api/v1/admin/roles`: returns system roles catalogue.
  - `PUT /api/v1/admin/users/{userId}/roles`: assign roles with guardrails (cannot remove self admin, cannot remove user role, reason required).
  - `PUT /api/v1/admin/users/{userId}/status`: activate/deactivate accounts with guardrails (cannot deactivate self, cannot deactivate last admin, immediate session revocation and security stamp rotation on deactivation).
  - `GET /api/v1/admin/users`: added `search` alias, case-insensitive email/displayName matching, role filter, active planCode and entitlementState filtering, and cursor pagination fix (`lastId`).
- Automated test coverage: verified 60/60 unit tests pass, 68/68 integration tests pass (including 8 comprehensive identity/admin tests in `IdentityAdminApiTests`). EF Core model has 0 pending changes.

## 2026-09-07 — Render Free staging deployment for frontend integration

- Added deployment infrastructure for Render Free hosting (`nexora-staging` Web Service on Docker runtime in Oregon region).
- Packaging: Multi-stage .NET 10 Dockerfile building `Nexora.Api`, `Nexora.Worker`, and EF Core migration bundle (`/app/nexora-migrate`).
- Co-location topology: API and Worker run as separate .NET executables inside the same container managed by `scripts/render-entrypoint.sh`.
- Architecture rationale: Current implementation uses `LocalStorageProvider`; co-location enables both API and Worker to share the same local filesystem (`/tmp/nexora-storage`).
- Storage is intentionally ephemeral: raw uploaded files are not durable across container restarts/spin-downs; Neon PostgreSQL data remains persistent.
- Production topology remains deferred: Production requires implementing shared durable object storage before splitting API and Worker into separate services.
- Environment & Adapters: `ASPNETCORE_ENVIRONMENT=Staging`, non-production Neon PostgreSQL, Google Gemini staging (`gemini-3.5-flash-lite`, `Features__Ai=true`), SePay Sandbox (`Features__Payment=true`).
- Security, Reverse Proxy & CORS: Added `ForwardedHeadersOptions` to recognize client IP behind Render's reverse proxy for rate limiting; explicit CORS for local origins (`http://localhost:3000`, `http://localhost:5173`, `127.0.0.1`), cross-site refresh cookie (`SameSite=None; Secure=true; HttpOnly=true`).
- Quality gates: 62/62 unit tests passed, 68/68 integration tests passed, 0 EF Core pending model changes.
- Remote verification suite passed 100% on `https://nexora-backend-q32b.onrender.com`:
  - Health checks: `/health/live` and `/api/v1/health` return HTTP 200 Healthy.
  - Auth: user registration, `HttpOnly; Secure; SameSite=None` cookie handling, `GET /api/v1/me`.
  - Scenarios: 12 scenario-library seed records verified.
  - Shared Filesystem: presigned upload -> raw PUT -> worker outbox pickup -> extraction -> resume `ready` completed in 15 seconds.
  - SePay Checkout: Basic plan checkout session generated pending order with `https://pay-sandbox.sepay.vn` redirect URL and no secret leakage.
  - Gemini AI Evaluation: interactive interview created, question generated, candidate answer submitted and evaluated live with rubric scores and follow-up question.

## 2026-09-07 — AI contract reliability, structured execution layer, and error recovery

- Investigated Render Staging incident: `POST /api/v1/interviews/{id}/answers` -> HTTP 503 `AI_OUTPUT_INVALID` on request `31f4242115f74208bcf482ca7fe3b673`.
  - Root cause: Gemini returned valid HTTP 200 after 5.54s, but server semantic validation failed due to fragile criteria casing/presence checks and inconsistent STAR applicability expectations on non-behavioral questions.
  - Follow-up generation was tightly coupled with answer persistence; any subsequent AI error dropped evaluated answer persistence.
  - Nested retries between provider adapter and calling services multiplied slow external calls.
- Implemented provider-neutral structured execution layer:
  - Added `IStructuredAiExecutor` and `StructuredAiExecutor` enforcing a strict global retry budget of at most 2 provider calls per purpose (1 initial call + 1 repair/retry).
  - Reduced `GeminiOptions.MaxAttempts` default from 2 to 1 to eliminate nested retry multiplication.
  - Added `AiOperationCatalog` with definitions for all 8 AI operations: `interview.question.generate`, `interview.answer.evaluate`, `interview.followup`, `interview.report`, `resume.profile`, `resume.analyze`, `scenario.evaluate`, `star.evaluate`.
  - Added semantic repair cycle: attempt 1 semantic failures inject a focused repair prompt specifying the exact contract violation; non-repairable failures fail fast.
- Hardened answer evaluation & rubric validation:
  - Canonical 4 criteria (`correctness`, `structure`, `completeness`, `clarity`) normalized to trimmed lowercase with 0–100 integer scores and non-blank evidence.
  - Server-authoritative STAR normalization: non-behavioral questions with model-returned STAR are normalized to `applicable = false` without failing; behavioral questions missing STAR attempt repair once; standalone `star.evaluate` requires `applicable = true`.
- Isolated follow-up question generation:
  - Follow-up question generation failure (AI error/timeout/rate-limit) never fails or discards an evaluated candidate answer.
  - When follow-up AI fails, `PracticeService` falls back to a deterministic, Nexora-owned follow-up question (<= 2,000 chars) and persists the answer successfully.
- Hardened context budgeting in `ResumeContextBuilder`:
  - Prioritizes candidate answer text, question text, and metadata above background context; compacts JD and resume profile summaries to prevent crowding out candidate input.
- Privacy & Logging Invariants:
  - Used high-performance `[LoggerMessage]` source generators without CA1848/CA1873 violations.
  - Safe internal diagnostics logged only (`failureReason`, `stage`, `attempt`, `correlationId`); candidate answer text, prompt bodies, and raw provider responses are never logged.
- Quality Gates & Test Verification:
  - `dotnet build Nexora.slnx --nologo`: 0 Warning(s), 0 Error(s).
  - `dotnet test tests/Nexora.UnitTests/Nexora.UnitTests.csproj`: 87 passed, 0 failed, 0 skipped.
  - `tests/Nexora.UnitTests/Ai/CanonicalRubricValidatorTests.cs`: 6 tests covering missing/duplicate criteria, scores, evidence.
  - `tests/Nexora.UnitTests/Ai/AiOperationCatalogTests.cs`: 7 tests covering STAR normalization and repair prompts.
  - `tests/Nexora.UnitTests/Ai/StructuredAiExecutorTests.cs`: 7 tests covering retry budget, repair recovery, and error mapping.
  - `dotnet test tests/Nexora.IntegrationTests/Nexora.IntegrationTests.csproj`: 75 passed, 0 failed, 0 skipped.
  - `tests/Nexora.IntegrationTests/AiContractReliabilityTests.cs`: 4 end-to-end tests reproducing staging incident, proving non-behavioral STAR normalization, follow-up fallback isolation, 2-call repair budget, and terminal failure behavior with 0 persistence.
  - `dotnet ef migrations has-pending-model-changes`: No pending changes to EF Core model.
  - Automated tests run 100% offline with zero live Gemini calls using enhanced `TestAiProvider`.

## 2026-09-07 — Canonical STAR semantic contract, evidence-first extraction, and follow-up aware context

- Investigated Render Staging regression on behavioral interview evaluation:
  - Gemini correctly recognized and quoted Action and Result in the general rubric criteria (scores 95 and 90), but marked `action.detected = false, score = 0, feedback = "Thiếu nội dung action"` and `result.detected = false, score = 0, feedback = "Thiếu nội dung result"` inside the structured STAR component evaluation.
  - Root cause: Divergent instructions between `interview.evaluate` and `star.evaluate`, question-focus bias causing LLM to only look for components mentioned in the question prompt (e.g. asking for Task causing Action/Result to be missed), lack of domain-specific technical examples (indexing, caching, log analysis, latency drops), and absence of strict evidence-first extraction rules.
- Unified canonical STAR semantic contract:
  - Created `StarSemantics.CanonicalInstructions` as a single shared constant defining Situation, Task, Action, and Result with concrete technical and behavioral examples (e.g., connection pool exhaustion, log/EXPLAIN analysis, adding B-tree index, Redis caching, latency reduction, post-mortem).
  - Enforced question-focus detachment: the LLM must scan the entire answer for all four components, irrespective of how the question was framed.
  - Embedded `StarSemantics.CanonicalInstructions` into both `interview.answer.evaluate` (`interview-eval-v4`) and `star.evaluate` (`star-eval-v3`).
- Hardened evidence-first extraction & server-side validation:
  - For every component: if concrete evidence exists in candidate text, model must return `detected = true`, quote exact evidence, and score 1..100. If absent, `detected = false`, `evidence = ""`, and `score = 0`.
  - Added `StarComponentValidator.Validate` in `AiOperationCatalog` ensuring invariants: `detected == false` strictly requires `score == 0`, and `detected == true` requires non-blank evidence and `score > 0`.
  - Server-authoritatively recomputes `overallScore` (Situation 20%, Task 20%, Action 35%, Result 25%) and evaluates `missingElements` (`!detected || score < 60`).
  - Added targeted repair prompts when model violates component invariants or detection-score parity.
- Implemented follow-up aware evaluation context:
  - Extended `IResumeContextBuilder.BuildAnswerEvaluationContext` and `ResumeContextBuilder` to pass `question-sequence`, `is-follow-up`, and `followup-target-elements` (populated from previous answer's missing elements).
  - Forwarded follow-up metadata through `PracticeService.SubmitAnswerAsync` into `AiOperationContext.Metadata`.
  - Added explicit instructions directing the model to evaluate all present components while recognizing when follow-up answers focus specifically on missing elements.
- Quality Gates & Test Verification:
  - `dotnet build Nexora.slnx --nologo`: 0 Warning(s), 0 Error(s).
  - `dotnet test tests/Nexora.UnitTests/Nexora.UnitTests.csproj`: 93 passed, 0 failed, 0 skipped.
    - Added tests for `StarComponentValidator`: Test A (`detected=false, score=60` fails), Test B (`detected=true, evidence=""` fails), Test C (valid undetected `score=0, evidence=""`), Test D (valid detected `score=90, evidence="quote"`).
    - Added prompt contract test (Test L) verifying both operations share `StarSemantics.CanonicalInstructions`.
    - Verified repair instruction generation for component invariant violations.
  - `dotnet test tests/Nexora.IntegrationTests/Nexora.IntegrationTests.csproj`: 77 passed, 0 failed, 0 skipped.
    - Added Test M verifying context builder includes question sequence, follow-up flag, and target missing elements.
    - Added regression fixture verifying follow-up aware evaluation preserves all detected STAR components.
    - Updated `TestAiProvider` to adhere to zero-score for undetected components invariant.
