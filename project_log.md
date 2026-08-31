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
- Added deterministic fake document extraction and structured fake AI analysis; the optional Gemini adapter is configuration-driven, sends its API key only in a request header and is blocked in Production pending DEC-01.
- Evidence: T-06 rejects an executable renamed to PDF with no final storage/database record; the valid private resume/JD path completes a versioned analysis.

## 2026-08-25 — Phase 3 interview lifecycle and quota boundary completed

- Added the canonical interview session/question/official-answer persistence model and `draft`-compatible `starting → active → completing → completed`, `starting → failed` state transitions with optimistic versioning.
- Added `IAiProvider.GenerateStructuredAsync<T>`, deterministic `FakeAiProvider`, bounded untrusted input, explicit response schemas and server semantic validation.
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
- Completed the Phase 4 source security review and made current Fake AI, Fake Payment and Local Storage/upload features fail closed in Production until their deferred decisions approve replacements.
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
- Ran API and Worker simultaneously against Neon PostgreSQL with Fake AI, Fake Payment and Local Storage; readiness became healthy.
- End-to-end smoke passed registration/login, `/me`, plans, duplicate verified fake webhook, entitlement, private CV/JD upload, worker extraction/analysis, interview activation, both official answers, completion/report, dashboard and persistence after API restart.
- PostgreSQL-specific transactional quota and atomic outbox-claim paths were exercised by the live flow; one order/report was observed through API projections and no duplicate processing surfaced.
- No staging/production deployment, provider decision, production backup/restore (T-10) or production-readiness claim was made.
- Status: **INTERNAL DEVELOPMENT ENVIRONMENT READY (NEON)**.

## 2026-08-25 — Gemini development contract verified

- Hardened `GeminiAiProvider` with configuration-driven timeout and bounded retry, provider-neutral failure categories, safe response handling and defensive structured-output parsing; no provider body or API key reaches Business/API errors.
- Added purpose-specific instructions for the exact Nexora rubric, grounded evidence and concise bounded report content while preserving server-side schema and semantic validation.
- Added 4 deterministic unit tests for request/schema isolation, bounded rate-limit retry, authentication-error normalization and invalid structured-response retry/failure. Default automated tests remain network-free with Fake AI.
- Added an explicit opt-in Gemini live smoke that uses local user-secrets, Neon development PostgreSQL and synthetic CV/JD/answers only.
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
- Added a Vietnamese FE setup/Swagger walkthrough covering shared per-machine user-secrets, Fake AI, API + Worker startup, auth, resume/JD/analysis, fake payment/interview, CORS and exact 409 troubleshooting; explicitly documented the current fake document-extraction limitation. An identical standalone copy was delivered on the user's Desktop.
- Final restore/build passed with 0 warnings/errors; 14 unit and 27 integration tests passed. Scoped formatting, whitespace and startup-script syntax checks passed; guide copies match.
- NuGet reported no known vulnerable direct/transitive packages. Secret-pattern scan and changed-file review found only placeholders, existing synthetic test credentials and secret-setting names; no live secret was added.
- Browser smoke on an isolated local API verified the loaded OpenAPI UI, Bearer control/header, canonical 401 for an invalid synthetic token, required idempotency input and PDF/DOCX file picker. This smoke used no live database or provider and does not claim a new Neon or production E2E run.
- Status: **READY FOR DEVELOPMENT SWAGGER PULL REQUEST**.

## 2026-08-31 — FE guide aligned with live Gemini development configuration

- Updated the Vietnamese FE/Swagger onboarding guide for the verified Development path: Gemini API key from user-secrets, model ID `gemini-3.5-flash`, PowerShell 7 smoke command, quota warning and troubleshooting for model/auth failures.
- Clarified that automated `dotnet test` remains deterministic Fake AI, payment remains fake for development, and `FakeDocumentExtractor` still limits real CV text-analysis fidelity.
- Updated the repository guide and its standalone Desktop copy; no connection string, API key or other secret was written to either document.

## 2026-09-01 — Real PDF/DOCX extraction and development storage alignment

- Replaced the Development `FakeDocumentExtractor` registration with a real PDF/DOCX text extractor using PdfPig and Open XML; image-only/scanned PDFs fail safely because OCR is not in scope.
- Added deterministic PDF and DOCX extraction tests and made the practice integration fixture a valid text PDF; the extracted content is asserted before analysis.
- Updated the Development runner to pass one absolute `.nexora-local/storage` path to both API and Worker, preventing relative-working-directory upload/extraction failures.
- Updated FE onboarding to explain real extraction and to stop when the Neon branch selector is `production`; Development secrets must use the dedicated Neon `development` branch.
- Verification: restore/build passed with 0 warnings/errors; 14 unit and 29 integration tests passed. Live Gemini/Neon verification remains opt-in and must target the development branch only.
