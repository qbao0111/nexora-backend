# Nexora Backend Implementation Log

This log records completed implementation milestones and verification evidence. It must never contain credentials or other secrets.

## 2026-09-11 — Remove ENDOFLINE CI gate (completed)

- Status: Completed
- Owner: Codex / repository tooling
- Branch: `chore/remove-endofline-gate`
- Scope: Removed the global ordinary-source CRLF requirement and split CI format verification into style and analyzer checks so LF-versus-CRLF alone is not a required failure condition. No application behavior changed and no files were mass-normalized.
- Files/modules: `.editorconfig`, `.github/workflows/backend-ci.yml`, `AGENTS.md`, `docs/04-production-runbook.md`, `implementation_plan.md`.
- Verification: Workflow/config diff inspection and `git diff --check`; relevant formatter commands are validated separately for this change.
- Dependencies: Build, tests, analyzer/style checks and security gates remain required.
- Remaining blockers: none identified.

## 2026-09-11 — Retire automated review tooling (completed)

- Status: Completed
- Owner: Codex / manual engineering workflow
- Branch: `chore/retire-c2c`
- Scope: Removed repository-side automated review configuration and returned future work to manual review. No application or product behavior changed.
- Files/modules: `.c2c.json` removed; `docs/c2c-review-policy.md` removed; `AGENTS.md` now states the concise manual review workflow.
- Verification: Repository cleanup audited with active-reference search and `git diff --check`.
- Dependencies: Human review handoff; no automated review loop or automatic merge.
- Remaining blockers: none.

## 2026-09-10 — C2C workflow optimization (in progress)

- Status: In progress
- Owner: Codex / local engineering workflow
- Branch: `chore/c2c-flow-optimization`
- Base: `519793d78aada05294177ae573dd545242fa1086` (`main`)
- Commit/PR: pending at entry creation; exact values belong in the C2C execution record after delivery
- Scope: Separate implementation iterations, semantic review and evidence refresh; keep deterministic/mechanical failures with Codex; require the applicable hosted-CI-equivalent gates before review; set the repository C2C profile to six genuine iterations. No application code or product semantics.
- Contracts/traceability: `.c2c.json`, `docs/c2c-review-policy.md`, global machine-local C2C policy
- Files/modules: `.c2c.json`, `docs/c2c-review-policy.md`, `project_log.md`; global policy remains outside the repository
- Verification: branch created from the fetched `origin/main`; C2C doctor green; `.c2c.json` valid with `maxIterations=6`; restore/build green; 296 unit and 149 integration tests passed; EF reports no pending model changes; vulnerability audit clean; no changed C# files require the formatter; `git diff --check` clean
- Dependencies: merge this tooling policy before beginning A5; A5 and A6 remain separate branches/PRs
- Remaining blockers: none identified; hosted CI and independent ChatGPT review are required before conditional merge

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

## 2026-09-07 — AI validation integrity follow-up

- Removed candidate-specific fabricated fallbacks from resume analysis, interview reports, scenario evaluation and standalone STAR validation. Required semantic fields now use the existing single repair attempt and fail safely if the repaired response remains invalid.
- Preserved safe STAR structural normalization for absent components and blank component feedback; no candidate evidence or assessment is invented.
- Rejected overlong first/follow-up questions through semantic repair instead of truncating provider output. Follow-up terminal failure still uses the existing deterministic Nexora-owned question so an already evaluated answer is not discarded.
- Removed provider-level generic array/cardinality instructions; each `AiOperationCatalog` operation remains authoritative for its own contract.
- Unified `ResumeProfile` normalization and usefulness validation across structured generation, cached profiles and Gemini document fallback. Summary is optional when another useful profile field exists; a completely empty profile remains invalid.
- Prompt/schema/model version identifiers remain unchanged because this patch restores the already documented operation contracts and removes a conflicting provider overlay; it does not introduce a new persisted result shape or rubric meaning.
- Added focused unit and integration regressions for semantic repair exhaustion, no fabricated persistence, question length handling, operation-owned prompt rules and canonical profile validity. No live Gemini call is required.

## 2026-09-07 — Enforced AI score-scale contract

- Made root `scoreScale` mandatory in the JSON schemas for interview answer evaluation, interview reports, scenario evaluation and standalone STAR evaluation.
- Removed DTO defaults that could turn an omitted provider field into an implicit valid value. Semantic validators now accept only the exact ordinal value `0-100`, repair invalid/missing values once, and explicitly preserve `0-100` in normalized output.
- Kept prompt/schema version identifiers unchanged because `scoreScale: "0-100"` was already the declared contract; this patch closes its enforcement gap without changing scoring semantics.

## 2026-09-07 — Optional DeepSeek V4 Flash text provider (local evaluation branch)

- Added `DeepSeekAiProvider` under `Nexora.Integrations` using the official HTTPS `api.deepseek.com/chat/completions` endpoint and the provider-neutral `IAiProvider` contract.
- Added configuration-driven `Ai:Provider` selection (`gemini` default or `deepseek`, unknown values fail closed). `Nexora.Api` and `Nexora.Worker` use the same selector; `IDocumentOcrProvider` remains `GeminiDocumentOcrProvider` regardless of text-provider selection.
- Added non-secret DeepSeek appsettings defaults with `MaxAttempts=1` and explicit per-purpose thinking/reasoning policy. The cost-aware baseline keeps high reasoning only for `interview.evaluate` and `star.evaluate`; no operation defaults to `max`.
- DeepSeek requests keep trusted operation metadata/instructions/schema in the system message and untrusted candidate input only in the user message. JSON mode, exact `max_tokens`, safe error normalization, timeout/cancellation handling and metadata-only usage telemetry are covered without logging keys, prompts, candidate text, response content or `reasoning_content`.
- Added offline fake-handler tests for endpoint/auth/request contract, all eight policies, configuration fail-closed behavior, response/error mapping, cancellation, usage parsing and provider selection. No live paid DeepSeek request was made.
- Verification: `dotnet restore`, `dotnet build Nexora.slnx --nologo` (0 warnings, 0 errors), 178 unit tests passed, 82 integration tests passed, `dotnet ef migrations has-pending-model-changes` reported no pending changes, and `git diff --check` is clean.
- This branch is for Development/local owner evaluation only. DEC-01 (production AI provider/model and budgets) remains deferred and is not a blocker for Phases 0–3 or local testing.

## 2026-09-08 — Story-level STAR report summary aggregation

- Corrected `report.starSummary` to treat question sequence 1 plus all follow-ups as one behavioral story under the current interview model.
- Report components now merge the strongest valid grounded evidence across the chain, recompute the server-authoritative STAR score with 20/20/35/25 weights, and never average or trust per-answer overall scores.
- Recomputed unresolved `recurringIssues` from merged component state instead of unioning historical `missingElements`; follow-ups can resolve earlier gaps without stale issues.
- Coaching priorities now use feedback attached to the merged weak components in deterministic order (distinct, max three) rather than flattening historical coaching tips.
- Report retrieval loads question sequence metadata with the session answers using a fixed split query; no `ParentQuestionId`, migration, or additional AI call was added. Realtime per-answer STAR evaluation remains unchanged.

## 2026-09-08 — DeepSeek reasoning-budget fallback

- Added a provider-neutral `AiProviderRetryHint.LowerReasoningEffort` and per-attempt `AiReasoningEffortOverride.Low` on the Business AI contracts. `StructuredAiExecutor` remains the global two-call owner and keeps `RepairUsed` reserved for semantic repair.
- `DeepSeekAiProvider` now emits the hint only for a positively confirmed exhaustion response: `finish_reason=length`, unusable structured content, and complete/reasoning usage at or above the request token budget with reasoning no smaller than completion. A usable JSON body remains a success, and inconclusive metadata keeps generic `InvalidResponse` behavior.
- The one fallback retry preserves operation/input/schema/instructions/`MaxOutputTokens`, temporarily sends `reasoning_effort=low` only for an enabled policy, and does not mutate options or add provider-internal retries. Normal `interview.evaluate`/`star.evaluate` high defaults remain unchanged.
- Added offline regressions for normal high success, the exact 6,000-token exhaustion incident, low-fallback terminal failure, semantic repair separation, generic invalid response, usable JSON with `finish_reason=length`, and disabled-reasoning fail-closed behavior. No live Gemini/DeepSeek request was made.
- Verification: `dotnet restore`; `dotnet build Nexora.slnx --nologo` (0 warnings, 0 errors); 189 unit tests passed, 86 integration tests passed, 0 failed/skipped; `dotnet ef migrations has-pending-model-changes` reported no pending model changes; `git diff --check` is clean.

## 2026-09-08 — Restrict DeepSeek reasoning fallback to high-policy exhaustion

- Restricted `LowerReasoningEffort` emission to confirmed exhaustion on an effective `high` DeepSeek policy. `resume.analysis`, `interview.report` and `scenario.evaluate` keep ordinary low-to-low `InvalidResponse` retry behavior without fallback telemetry.
- Tightened `StructuredAiExecutor` so the reasoning override requires both `AiProviderRetryHint.LowerReasoningEffort` and `AiProviderFailureKind.InvalidResponse`; timeout/rate-limit/unavailable hints cannot schedule a LOW override.
- Added offline regressions for low-policy hint suppression, low-to-low retry, and non-`InvalidResponse` hint rejection. Defaults, token budgets, provider `MaxAttempts=1`, semantic repair, STAR behavior and configuration remain unchanged.
- Verification: 192 unit tests passed, 86 integration tests passed, build 0 warnings/0 errors, EF reports no pending model changes, and no live paid AI request was made.

## 2026-09-08 — Hardened DeepSeek reasoning fallback regressions

- Corrected the low-policy retry regression to use the `resume.analysis` operation budget (`1,500` tokens) and to distinguish provider usage/exhaustion telemetry from structured-executor retry telemetry.
- Added request recording to prove the high-policy fallback changes only the per-attempt reasoning effort while preserving input, trusted instructions/schema, messages and `max_tokens`; low-to-low retries keep both `AiRequest.ReasoningEffortOverride` values null.
- Expanded provider-neutral retry-hint coverage for timeout, rate-limit and unavailable failures, preserving exactly two executor attempts with `RepairUsed=false` and no reasoning override.
- Verification: focused DeepSeek/executor unit tests passed (52 tests); a mutation removing the provider high-policy guard made the low-policy test fail on an unexpected `Low` override, then the guard was restored with no production diff. No live AI request was made.
- Lead verification: `dotnet restore`; solution build (0 warnings/errors); full unit suite (194 passed, 0 failed/skipped); integration suite (86 passed, 0 failed/skipped); scoped `dotnet format --verify-no-changes`; EF pending-model check (none); `git diff --check`. Only the two AI test files and this log changed.

## 2026-09-08 — Authenticated SignalR resource notifications

- Added authenticated `/hubs/realtime` with JWT `sub` user routing and no client-controlled subscription methods. Query `access_token` is accepted only under the hub path; existing JWT/security-stamp/user validation is preserved. Connections close on token expiration and query-bearing framework request/transport logs are suppressed.
- Added `realtime_notifications` and an additive EF migration. Resume ready/failed, resume-analysis completed/failed and interview active/failed/completed notifications are inserted with the existing state transaction. No intermediate events or business/REST response changes were introduced.
- API-only broadcaster sends the five-field `resourceChanged` payload to the owner, marks processed after send and retries failures with a due timestamp. Event IDs remain stable on retries; delivery never mutates quota or practice data. Worker has no SignalR dependency. Supports one API instance.
- Owner explicitly chose no report-job-failure event: interview remains `completing` with the existing free retry/credit semantics. Account deletion cleans up the new notification metadata.
- Added frontend documentation for deduplication, one REST refetch per relevant event, initial/reconnect reconciliation, slow fallback polling, disabled realtime, backlog and migration rollout. No production deployment or live database migration was performed.
- Verification: `dotnet restore`; solution build (0 warnings/errors); 194 unit and 104 integration tests passed (0 failed/skipped). New coverage includes real in-process WebSocket handshakes/events, owner isolation, token scoping/revocation, retries/duplicates, disabled realtime, worker ready/failure transitions and atomic rollback on notification write failure. Normal integration tests use SQLite and deterministic providers; no live paid AI calls. EF reports no pending model changes and the generated PostgreSQL migration SQL contains only the new table/index and migration history entry.

## 2026-09-09 — Parallel-work synchronization guidance

- Added implementation-plan rules requiring each teammate to read `project_log.md` and verify dependency branch/commit/PR status before starting a dependent task.
- Required completed tasks to record date, branch, commit/PR, scope, tests and remaining blockers in the log so parallel work has one shared readiness ledger.
- Added the same workflow to `README.md`, including an AI-oriented preflight sequence, a copyable log-entry template and rules to keep secrets, sensitive user data and unverified claims out of the ledger.

## 2026-09-09 — CV upload and resume-analysis retry correction (working tree)

- `resume.analysis` now starts at effective 4,096 output tokens and selects one validated 8,192-token retry only for the provider-neutral `OutputTruncated` hint; DeepSeek rejects `finish_reason=length` before parsing content, while high-policy reasoning fallback remains unchanged.
- Hardened local CV upload with bounded exact-byte reads, stable size/type/signature/container errors, real PDF/OpenXML checks, bounded DOCX ZIP/XML validation, safe metadata-only rejection logs, cancellation cleanup and single-use concurrent PUT gating. The private storage provider removes partial files after failed/cancelled writes.
- Added FE operation coordination with user-scoped persisted idempotency state, same-key JD/analysis replay, auth-gated recovery and in-flight deduplication. Upload uses one File snapshot, MIME fallback, abort/generation guards and safe error envelopes without bearer headers on raw storage URLs.
- Added focused AI/upload regressions and updated the old synthetic PDF worker fixture to a real blank PDF. Gemini adapter attempts are hard-bound to one so the structured executor remains the single global two-call owner; valid encrypted PDFs are accepted at upload and deferred to extraction/OCR. No migration or provider-specific API contract was introduced.
- Verification: full unit suite 204 passed and full integration suite 104 passed; solution build has 0 warnings/errors. Focused coverage includes Gemini single-adapter-attempt validation and encrypted-PDF upload acceptance; FE `tsc --noEmit`, touched-file ESLint and `next build` pass. `npx react-doctor@latest` completed (full score 74/100; changed scope 78/100) with warnings about the existing large/complex resume page and intentional user-scope state reset. Repository-wide FE lint still has unrelated pre-existing errors outside the touched files; no live AI calls.

## 2026-09-10 — B7 Scenario Practice v2

- Branch: `feat/scenario-practice-v2`; implementation commit: `16df6b7 feat(scenario): implement scenario practice v2`.
- Added category/track catalogue and filters, difficulty-aware progression, idempotent same-scenario retry, owner-scoped newest-first attempt history, latest-versus-previous comparison, and scenario progress aggregates by track, competency and difficulty.
- API contracts: `GET /api/v1/scenarios/categories`, extended scenario filters, `POST /api/v1/scenarios/{scenarioId}/retry`, `GET /api/v1/scenarios/{id-or-slug}/attempts`, and `GET /api/v1/scenarios/progress`. History exposes only stored 0–100 overall scores and comparison fields; raw evaluation JSON is excluded from the history projection.
- Competency aggregation uses the scenario's existing `Competency` value and only completed attempts with a valid stored `EvaluationJson.overallScore`; failed/incomplete/invalid-score attempts are excluded from score aggregates and user ownership is enforced in every query.
- Migration impact: none. Existing Scenario/ScenarioAttempt schema and JSON evaluation evidence were reused; no DbContext, migration, ModelSnapshot, Program, Interview or `PracticeService.cs` file was changed. Bookmark is deferred because it needs a user-scenario schema/migration.
- Verification: solution build passed with 0 warnings/errors; full suite passed with 210 unit and 109 integration tests; B7-specific coverage passed 5/5; Scenario/STAR/SignalR regression filter passed 29/29; scoped `dotnet format --verify-no-changes` and `git diff --check` passed. Tests use SQLite and deterministic AI only; no live provider calls.
- Remaining dependency/blocker: none for B7. Frontend must consume the new response fields/endpoints; bookmark remains explicitly out of scope.

## 2026-09-10 — A1 Cloudflare R2 storage provider

- Status: Merged
- Owner: Backend workstream A
- Branch: `feat/r2-storage-provider`
- Commit/PR: `362e040001e12ee14bc1380109dbb7da5a6aecf7` / `#42` (merged to `main`)
- Scope: Added the provider-neutral R2 implementation, configuration/selection and key validation; no A2 presigned upload flow, migration, domain semantics or live provider calls.
- Contracts/traceability: `IStorageProvider`; A1 in `implementation_plan.md`; ADR-004 and storage sections in `SPEC.md`, `docs/02-architecture.md` and `docs/04-production-runbook.md`.
- Files/modules: `src/Nexora.Integrations/Storage/*`, `src/Nexora.Business/Storage/StorageKeyValidator.cs`, integration DI/production safety, API/Worker storage config, package manifest and focused storage tests.
- Verification: `dotnet restore`; `dotnet build --no-restore Nexora.slnx --nologo` (0 errors, 0 warnings); 265 unit tests and 133 integration tests passed; EF reports no pending model changes; vulnerable-package scan reports none; scoped `dotnet format --verify-no-changes` and `git diff --check` pass. Full solution format still reports 18 pre-existing issues outside this A1 scope.
- Dependencies: A2 — production upload intents/presigned PUT/finalize flow; DEC-04 final production account/hosting enablement. No migration impact.

## 2026-09-10 — A2 production R2 upload intent (PR candidate)

- Status: PR open; hosted CI green; remote review pending
- Owner: Backend workstream A
- Branch: `feat/a2-production-upload`
- Base: `3d21cff7e4665914f9cc169309b53a682f64e345` (`main`)
- Ancestry evidence: after `git fetch origin`, `HEAD`, `origin/main` and `git merge-base HEAD origin/main` all resolve to `3d21cff7e4665914f9cc169309b53a682f64e345`.
- Commit/PR: implementation `f85a4695b948d13b43a4519ff2431f105bd92642`; formatting corrective `634d08a91e6402c5e773d3216b94eea9610676cb` / `#45` (open)
- Scope: Replaced the production in-memory upload-intent path with durable PostgreSQL intent state, short-lived private R2 signed PUT URLs and idempotent finalize validation. Local server-side upload remains the Development/Testing path; no Scenario/STAR, Interview or CV-analysis semantic changes were made.
- Contracts/traceability: `IUploadIntentStore` owns durable token-hash state; R2 `POST /api/v1/uploads/presign` issues an independent random capability without a presign idempotency header, and `POST /api/v1/resumes` remains the replay-safe finalize boundary. `implementation_plan.md` A2/A3, ADR-004, `SPEC.md` and `docs/03-api-data-contract.md` are aligned.
- Security decisions: token hashes only in the database; owner and expiry checks; private signed PUT without public ACL; actual R2 object metadata/bytes are revalidated for size, PDF/DOCX signature/container and checksum; provider errors/logs exclude credentials, signed URLs and file contents; worker rechecks stored-file size/checksum.
- Files/modules: upload intent Business/Data contracts and migration, R2 upload provider/object client, shared document validator, DI/provider selection, production upload safety gate, resume concurrency/integrity and privacy deletion, focused unit/integration tests and related documentation.
- Verification: Release solution build passes with 0 errors and 0 warnings; full suites pass with 280 unit tests and 143 integration tests. Focused R2 storage/presigner (19), R2 upload provider (8), durable-intent (3), R2 API flow (5), integrity and privacy-race regressions pass. `dotnet ef migrations has-pending-model-changes` reports no pending model changes; the vulnerable-package audit is clean; changed-file `dotnet format --verify-no-changes` and `git diff --check` pass. Hosted Backend CI run `34438975258` is green for exact head `634d08a91e6402c5e773d3216b94eea9610676cb`, including tracked-repository cleanliness. No live R2, AI, payment or production secrets are used.
- Migration impact: added `20260910035047_ProductionUploadIntents`; no other schema changes.
- Dependencies: A2 review/merge; A4 CV Analysis 2 modes follows after A2. DEC-04 final production account/hosting enablement remains deferred. No live R2, paid AI or production deployment was used.

## 2026-09-10 — Backend GitHub Actions CI

- Status: Completed
- Owner: Backend engineering
- Branch: `chore/backend-ci`
- Commit/PR: `ba90e974d126b90f70f6e7c7c19e654ad8a19f7a` / `#43`
- Scope: Added one least-privilege GitHub Actions workflow for PRs to `main`, pushes to `main` and manual dispatch. No application behavior, provider semantics, migration or deployment path changed.
- Contracts/traceability: `NFR-QUAL-01` and the canonical quality gates in `docs/05-test-strategy.md`; repository `.editorconfig`, `global.json` and local `dotnet-ef` tool manifest remain authoritative.
- Files/modules: `.github/workflows/backend-ci.yml`; this log entry only.
- Verification: YAML parsed successfully and all nine shell steps passed Bash syntax validation; `dotnet tool restore`; `dotnet restore Nexora.slnx`; Release build passed with 0 warnings/errors; 235 unit and 133 integration tests passed; EF reported no pending model changes; .NET 10 JSON vulnerability audit reported no vulnerable direct/transitive packages; changed-file format selection and scoped format passed; `git diff --check` passed. GitHub Actions run `34420764114` for PR `#43` completed successfully in 2m09s with every workflow gate green. The intentionally non-blocking full-repository format probe still reports 18 pre-existing findings outside this CI diff.
- Dependencies: None. Integration tests remain isolated through in-memory SQLite/WebApplicationFactory and deterministic providers; no PostgreSQL/Neon, R2, Resend or production secret is required.
- Remaining blockers/follow-up: None for the workflow. Configure the stable `Backend CI / Build, test, and validate` check as required in the GitHub `main` Ruleset.

## 2026-09-10 — Codex with ChatGPT workspace workflow

- Status: Completed
- Owner: Codex / local engineering workflow
- Branch: `chore/c2c-review-workflow`
- Setup origin: Workspace setup/testing began on `feat/r2-storage-provider`; this tooling delivery belongs to `chore/c2c-review-workflow`.
- Commit/PR: `195a339225fcf59d740352c55b8f9de650bcb469` / `#44`
- Scope: Added the Nexora C2C profile and repository-specific review policy, configured the reusable global C2C skill/policy outside the repository, and connected the `NexoraBackend` ChatGPT Project through the temporary connection. No application code, provider decision, deployment, merge or A2 implementation was performed.
- Contracts/traceability: `AGENTS.md`, `README.md` team workflow, `SPEC.md`/`docs/README.md` ownership hierarchy, and `implementation_plan.md` remain authoritative.
- Files/modules: `.c2c.json`, `docs/c2c-review-policy.md`, `project_log.md`; global files are under the user Codex C2C configuration directory.
- Verification: upstream C2C `pnpm install`, `pnpm build`, and `pnpm test` passed (170 tests); `c2c doctor` passed for bridge, OAuth and quick connection; saved Project/chat binding is in project mode; ChatGPT boot and `workspace_info` plus a hello-style top-level file read returned workspace `NexoraBackend`.
- Dependencies: None. The connector is scoped to the `NexoraBackend` workspace and project-only memory; repository sources were not uploaded.
- Remaining blockers/follow-up: PR `#44` is open; final-head Backend CI and C2C review are required before human merge. The current quick connection is temporary and may need a fresh setup/pairing after restart or expiry; a named Cloudflare connection is optional future setup, not required for local use.

## 2026-09-10 — Conditional C2C auto-merge policy

- Status: PR `#46` is open; this policy metadata remains subject to the exact-head merge gate
- Owner: Codex / local engineering workflow
- Branch: `chore/c2c-conditional-auto-merge`
- Base: `3d8848127963c3ccb6e70a335c79628fc885974a` (`main` after A2 merge)
- Commit/PR: `c67c8acb1f1a7bac67be91df5f05a255f03fffb9` / `#46` (open)
- Scope: Updated the Nexora repository C2C policy with an explicit conditional auto-merge gate while keeping human-only merge as the generic default. The machine-local generic policy and NexoraBackend ChatGPT Project instructions were updated separately; ChatGPT remains review-only and production deployment is not authorized.
- Contracts/traceability: `docs/c2c-review-policy.md`; exact-head `READY_TO_MERGE` evidence, fresh hosted checks, open/non-draft/mergeable PR, unchanged base and scope, and normal non-bypass merge are required. No application, provider, migration or A4 changes.
- Files/modules: `docs/c2c-review-policy.md`, plus the factual project log entry; global policy is `C:\Users\PC\.codex\c2c\generic-review-policy.md` and Project instructions are stored in the NexoraBackend ChatGPT Project.
- Verification: policy diff and `git diff --check` pass; C2C doctor is green and Project instructions saved. Hosted CI run `34442473665` was green for the prior head `9b26515c426675d5282a38490b502cfeb802c94e`; this log-only correction requires fresh exact-head CI and independent ChatGPT remote review before merge.
- Dependencies: None. After this tooling policy is merged, fetch latest `main` and start A4 on a fresh `feat/a4-cv-analysis-v2` branch; do not start A5.

## 2026-09-10 - A4 CV Analysis v2

- Status: PR `#47` open; hosted CI GREEN for the verified exact code head; C2C remote review pending
- Owner: Backend workstream A
- Branch: `feat/a4-cv-analysis-v2`
- Base: `5c731fb028e59b67b54f525f8d79104623b337a1` (`main`)
- Code commit/PR: `348d85660ca78cbe336fecd212b1bc8eda474672` / `#47` (open); verified exact PR head: `1edd625b4ca1a27034c2ebe5995607488325e766`
- Scope: Added explicit `job_targeted` and `field_benchmark` analysis modes, optional JD/context contracts, persisted mode/context/profile snapshots and execution provenance, strict provider-neutral schemas and semantic validation, and one bounded 4096->8192 truncation retry. Added the A4 EF migration only; no A5, Scenario/STAR, Interview, Billing or provider-selection changes.
- Security/reliability: Existing owner authorization, stable idempotency fingerprint, quota reservation/consumption, outbox and realtime behavior remain authoritative and unchanged. No live AI/provider calls or secrets are used.
- Verification: Release restore/build passed with 0 warnings/errors; 296 unit tests and 149 integration tests passed sequentially (including field-benchmark truncation and mixed-context regressions); EF reports no pending model changes; vulnerable-package audit is clean; exact changed-file `dotnet format --verify-no-changes` passed after the final corrections (including formatter-required existing lines in the touched ProductPlatform test file); `git -c core.whitespace=cr-at-eol diff --check` passed. One initial parallel integration run hit a Windows file lock and was rerun sequentially successfully.
- C2C evidence: `c2c_a4f1` iteration 3 execution summary/output was recorded for connector review; ChatGPT returned `STATE: LOCAL_ACCEPTED` after independently checking the workspace and current diff.
- Remote gate: hosted Backend CI run `34456351413` passed for the verified exact head above; the PR was open, non-draft and mergeable at capture time. This log update is metadata-only; refresh exact-head evidence after it is pushed.
- Dependencies: A4 comparison remains future work; A5 free quota remains unchecked. C2C remote review remains required before merge.

## 2026-09-10 - A5 shared free CV analysis quota (implementation)

- Status: Implementation complete; local semantic review accepted; commit/remote review pending
- Owner: Backend workstream A
- Branch: `feat/a5-free-cv-analysis-quota`
- Base: `8bf2214add0f12bdb31c853376d287cf44b72e86` (`main` after C2C workflow optimization merge)
- Commit/PR: pending at entry creation; exact remote values belong in the C2C execution record
- Scope: Verified and hardened the existing account-level Free `cv_analysis` entitlement so `job_targeted` and `field_benchmark` share exactly one allowance. Reservations remain transactional and immutable; only a usable completed result consumes, pre-result worker failure voids, and idempotent or competing requests cannot add a second reservation/job.
- Contracts/traceability: A5 in `implementation_plan.md`; shared allowance and `FEATURE_QUOTA_EXCEEDED` behavior in `docs/03-api-data-contract.md`; `IFeatureEntitlementService` remains the quota boundary.
- Files/modules: `src/Nexora.Data/Billing/FeatureEntitlementService.cs`, `tests/Nexora.IntegrationTests/AuthApiTests.cs`, `tests/Nexora.IntegrationTests/ProductPlatformApiTests.cs`, `implementation_plan.md`, `docs/03-api-data-contract.md`, `project_log.md`.
- Verification: Focused Free quota integration coverage passed (7 tests), including both mode orderings, pre-result failure void/refund, same-key replay, cross-mode distinct-key concurrency and paid-limit configuration. Full Release build passed with 0 warnings/errors; 296 unit and 156 integration tests passed; EF reports no pending model changes; the vulnerable-package audit is clean; changed-file `dotnet format --verify-no-changes` passed; `git -c core.whitespace=cr-at-eol diff --check` passed. C2C execution records for iterations 1 and 2 contain the exact gate summaries; iteration 2 is the local-review correction only.
- Migration impact: none; existing entitlement, usage-event and idempotency schema reused.
- Dependencies: A5 review/merge; A6 Interview Contract v1 follows from latest `main`. No frontend changes.

## 2026-09-10 - A6 Interview Contract v1 (implementation)

- Status: Implementation complete; deterministic verification green; C2C local review checkpoint
- Owner: Backend workstream A (migration owner: Bảo)
- Branch: `feat/a6-interview-contract-v1`
- Base: `72f072b9dcbc7ec8ad955329e0268d2e2760e391` (`main` after A5 merge)
- Scope: Added explicit server-owned interview question `kind` (`primary`/`followup`), `topic`, and nullable `ParentQuestionId` lineage; validated parent/session/topic relationships; and grouped report STAR evidence by explicit story root instead of sequence position.
- Migration: `20260910105214_InterviewQuestionContractV1` adds the question columns, self-reference/index/check constraints, and backfills existing rows without changing unrelated schemas.
- Contracts/traceability: `docs/03-api-data-contract.md`, `docs/08-data-model.md`, `docs/09-ai-integration-spec.md`, and the A6 section of `implementation_plan.md` now define sequence as ordering only. The reserved free primary topics remain `self_introduction`, `behavioral_star`, `motivation_role_fit`; complete Q1–Q3/paywall flow remains A7.
- Verification: Release build passed with 0 warnings/errors; 300 unit tests and 159 integration tests passed; EF reports no pending model changes; changed-file C# format verification and `git diff --check` are clean; NuGet vulnerability audit is clean; no live provider calls. Iteration 2 corrected root-sequence tie-breaking and synchronized this record with the final local gate counts.
- Dependencies: A6 must pass exact-head local/remote review before merge; do not start A7. No frontend changes.

## 2026-09-10 - A7 Free interview + paid continuation (implementation)

- Status: Implementation complete; deterministic verification green; C2C local review pending
- Owner: Backend workstream A (migration owner: Bảo)
- Branch: `feat/a7-interview-free-continuation`
- Base: `c3af51190da78c382c22a9f3294400559cbd22e2` (`main` after A6 merge)
- Scope: Added the server-owned three-question Free trial (`self_introduction`, `behavioral_star`, `motivation_role_fit`), finish-now/upgrade continuation state, and an idempotent same-session paid `/interviews/{id}/continue` endpoint with entitlement-gated primary/follow-up generation. Paid question limits are policy data and remain separate from session quota; question lineage and STAR grouping continue to use explicit kind/topic/parent metadata.
- Contracts/traceability: Updated `SPEC.md`, `docs/03-api-data-contract.md`, `docs/08-data-model.md`, `docs/09-ai-integration-spec.md`, `docs/SRS.md`, `docs/11-analysis-design-models.md`, `docs/admin-api.md`, and the A7 checklist in `implementation_plan.md`.
- Migration: `20260910132606_InterviewFreeContinuation` seeds `interview_question_limit` policy values (Free 3, Basic 6, Weekly 8, Pro 10) and backfills entitlement feature snapshots for existing entitlements; no unrelated schema changes.
- Verification: `dotnet tool restore`; `dotnet restore Nexora.slnx`; Release solution build passed with 0 warnings/errors; 305 unit and 162 integration tests passed; focused A7/AI/realtime filter passed 36 tests; EF reports no pending model changes; NuGet vulnerability audit is clean; exact changed-C# `dotnet format --verify-no-changes` passed; `git diff --check` passed. Tests use deterministic providers and SQLite; no live AI/payment/provider calls.
- Dependencies: A7 exact-head local/remote review and merge; A8 per-answer coaching follows only after A7 merge. No frontend changes and no A8/A9 implementation was started.

## 2026-09-11 — C2C workflow optimization baseline

- Status: Policy update ready for review; no product implementation changes
- Owner: Codex / local engineering workflow
- Branch: `chore/c2c-flow-optimization-v2`
- Base: `ffc7d570d11f4ec2da2d45bf2ad15a474f35d3cb` (`main` after A7 merge)
- Scope: Reconciled the global and Nexora C2C policies around adaptive semantic review, risk-based validation, exact-head evidence reuse, durable handoff, declared task-sequence merge authorization, and the per-task `C2C_MODE: AUTO` / `C2C_MODE: MANUAL_RELAY` override. `project_log.md` remains a durable implementation record and no longer requires post-CI metadata churn.
- Files/modules: `docs/c2c-review-policy.md`, machine-local `C:\Users\PC\.codex\c2c\generic-review-policy.md`, `.c2c.json` unchanged.
- Verification: policy diff inspection and `git diff --check` are required before commit; no application build/test or EF migration work is implied by this docs-only change.
- Dependencies: A7 is merged as PR `#51` with merge commit `ffc7d570d11f4ec2da2d45bf2ad15a474f35d3cb`; A8 may start from the latest `main` after this policy baseline is accepted. No frontend changes and no A8/A9 implementation started.

## 2026-09-10 - A7 corrective iteration 2

- Status: Corrective implementation complete; deterministic gates green; C2C local review handoff pending
- Owner: Backend workstream A (migration owner: Bao)
- Task/checkpoint: `c2c_a7b3`, corrective iteration `2`; existing A7 task and branch preserved after a control-plane stall
- Branch/base: `feat/a7-interview-free-continuation` from `c3af51190da78c382c22a9f3294400559cbd22e2`; no commit or PR created yet
- Findings resolved: first-question prompt v3 and deterministic provider fixtures now honor server-owned topics; continuation remains `in_progress` while an issued cap question is unanswered; persisted question provenance comes from each `AiExecutionResult`; fake verified-payment checkout and distinct-key continuation concurrency coverage prove one paid question and one interview usage reservation/consumption.
- Policy: repository and machine-local C2C policies now define bounded review handoff/recovery plus the `EXECUTION_STALLED` progress watchdog. A stall is control-plane state only and does not replay implementation or consume an iteration.
- Verification: Release build passed with 0 warnings/errors; 306 unit and 164 integration tests passed; EF reports no pending model changes; NuGet vulnerability audit is clean; changed-file C# format verification and `git diff --check` pass. Tests use deterministic providers/SQLite; no live AI, payment or production provider calls.
- Scope: A7 corrective files only, plus the required policy/log evidence; no A8/A9 or frontend changes. The A7 migration remains unchanged.
- Next: submit exactly one `STATE: EXECUTED` handoff for iteration 2, obtain bounded C2C local review, then continue the existing commit/push/PR/CI/remote-review lifecycle.

## 2026-09-10 - A7 corrective iteration 3

- Status: One focused review correction complete; deterministic gates green; C2C local review handoff pending
- Task/checkpoint: `c2c_a7b3`, corrective iteration `3`; this increment reflects the concrete remaining review finding, not the earlier control-plane stall
- Finding closed: the verified checkout/webhook path now exercises a finite paid `interview_question_limit` through Q4/Q5, asserts terminal `max_questions_reached` plus `isComplete=true`, and verifies an over-cap continuation returns `INTERVIEW_MAX_QUESTIONS_REACHED` without another AI call, question, interview reserve or consume event.
- Verification after the new regression: Release build passed with 0 warnings/errors; 306 unit and 165 integration tests passed (20 PracticeApiTests); EF remains at no pending model changes; changed-C# format and `git diff --check` are clean. Existing vulnerability-audit evidence remains valid because no dependency changed.
- Next: send exactly one iteration-3 `STATE: EXECUTED` handoff for the same task and request the bounded local semantic review; no A8/A9 work has started.

## 2026-09-10 - A7 corrective iteration 4 (mechanical CI formatting)

- Status: Mechanical CI correction complete; local and remote semantic review accepted; PR ready for merge pending the repository approval gate
- Task/checkpoint: `c2c_a7b3`, corrective iteration `4`; hosted run `34502852688` failed only on `ENDOFLINE` for six changed C# files because their committed blobs were LF while `.editorconfig` requires CRLF.
- Correction: normalized only the six reported C# files to CRLF, preserving their content and all A7 behavior; no new review finding was introduced.
- Commit/PR: `5f66898e4e7e25d5163b0e11fa95efa83d687afd` on `feat/a7-interview-free-continuation`; PR [#51](https://github.com/qbao0111/nexora-backend/pull/51) targets `main`.
- Verification: local exact changed-file formatter, Release build (0 warnings/errors), 306 unit tests, 165 integration tests, EF no pending model changes, NuGet vulnerability audit, and `git diff --check` pass. Hosted Backend CI run `34504172946` is green for the exact head; ChatGPT remote review returned `STATE: DONE`, `VERDICT: READY_TO_MERGE`, `PR: 51`, `HEAD: 5f66898e4e7e25d5163b0e11fa95efa83d687afd`, `CI: GREEN`.
- Next: await explicit human authorization before merging PR #51; no A8/A9 work has started.

## 2026-09-11 - B9 Career Goal / Target Role (current-main synchronization)

- Status: Implementation complete / Ready for independent review
- Owner: Bảo Nguyên — Backend workstream B
- Branch: `feat/career-goal`
- Base: `ffc7d570d11f4ec2da2d45bf2ad15a474f35d3cb` (`origin/main`, after A4/A5/A6/A7)
- Implementation commit: `629ff824b8645900bd080c9d966b7a51aef9498e`
- Scope: Reapplied the reviewed B9 user-owned Career Goal API/domain/persistence foundation directly on the current main baseline. Existing B9 semantics are preserved: authenticated owner scope, safe 404 isolation, canonical seniority/optional normalization, target JD ownership checks, PATCH omitted-versus-explicit-null behavior, one active goal per user, atomic activation switching and no delete/archive endpoint. No B10+ logic, AI coupling, interview changes or `PracticeService.cs` changes were introduced.
- API contracts: `POST /api/v1/career-goals`, `GET /api/v1/career-goals`, `GET /api/v1/career-goals/{id}` and `PATCH /api/v1/career-goals/{id}`. Required `targetRole`/`seniority`, optional industry/company/owned target JD/ISO target date, standard `{data}`/`{error}` envelopes and owner-scoped response fields are documented.
- Domain/business rules: New goals are active and deactivate the user's prior active goal in one transaction. Service-level user-row locking plus the database filtered unique index prevent multiple active goals; nullable PATCH fields clear only when explicitly sent as `null`. Privacy export includes Career Goals and account deletion removes them.
- Files/modules: B9 API contracts/controller, `Nexora.Business.Career`, `Nexora.Data.Career`, DbContext/DI, privacy contract/service integration, API/data-model/frontend docs, focused tests and migration-chain assertions. A4 privacy export metadata, A6 interview question lineage/configuration and A7 interview continuation behavior remain intact.
- Migration impact: Removed stale `20260910072100_B9CareerGoal` artifacts and generated additive `20260910162014_B9CareerGoal` from the current main snapshot. The synchronized chain keeps A7 `20260910132606_InterviewFreeContinuation` before B9; the B9 migration creates only `career_goals` with owner/JD restrictive FKs, target constraints, UTC timestamps, owner/created and JD indexes, and `IX_career_goals_one_active_per_user` filtered on `Active = TRUE`. No existing A7 migration was modified.
- Verification: Post-synchronization `dotnet restore` passed; `dotnet build Nexora.slnx --nologo` passed with 0 warnings/errors; `dotnet test` passed with 312 unit and 169 integration tests, 0 failed/skipped; `dotnet ef migrations has-pending-model-changes` reported `No changes have been made to the model since the last migration.`; exact changed-C# `dotnet format --verify-no-changes` passed; NuGet vulnerability audit reported no vulnerable packages; `git diff --check` and the CRLF-aware staged diff check passed. No live AI, R2, Resend, payment, production database or production service calls were made.
- FE impact: No frontend code changed. The existing contract docs describe the endpoints, active-goal ordering and explicit-null PATCH behavior for future UI integration.
- Remaining blockers: Latest `origin/main` at `ffc7d570d11f4ec2da2d45bf2ad15a474f35d3cb` includes A7 and was verified not to contain B8 Star Story entities or migrations; the old B8 branch was not used and no B8 assumptions were added to B9. B9 is self-contained; clarify B8 main ancestry before dependent B10 work. Independent review remains required.

## 2026-09-11 - A8 Per-answer coaching

- Status: Implementation complete; deterministic verification and independent review pending
- Owner: Backend workstream A
- Branch: `feat/a8-per-answer-coaching`
- Base: `6150717f15c563241cce24314bea7cf2cc2f7d23` (`origin/main`, after A7 and C2C policy merge)
- Scope: Extended the provider-neutral `interview.evaluate` contract with grounded `strengths`, actionable `improvements` and a safe `improvedAnswer` in the same AI call. Added bounded schema/semantic validation, answer-fact grounding checks and server persistence through the existing evaluation payload; no second rewrite call, quota change or migration.
- Contracts/traceability: `docs/03-api-data-contract.md`, `docs/09-ai-integration-spec.md`, and the A8 checklist in `implementation_plan.md` define the response fields, limits and no-fabrication rules. Prompt/schema provenance is `interview-eval-v5`.
- Files/modules: `AiContracts`, `AiOperationCatalog`, `PracticeService`, deterministic AI test provider, AI catalog/provider reliability tests and interview API regression fixtures.
- Verification: Focused AI unit coverage passed (81 tests), Practice API regression coverage passed (20 tests), and focused AI integration coverage passed (7 tests); Release build passed with 0 warnings/errors; complete Release suites passed (317 unit, 169 integration); EF reports no pending model changes; changed-C# format verification, vulnerability audit and `git diff --check` are clean. Tests use deterministic providers/SQLite; no live AI/provider calls.
- Migration impact: none; existing interview answer JSON storage is reused and historical STAR parsing remains compatible.
- Dependencies: A8 review/merge is required before A9 report production. No frontend changes and no A9 implementation was started.

## 2026-09-11 - A8 corrective iteration 2

- Status: Corrective implementation complete; awaiting the same-task semantic re-review
- Task/checkpoint: `c2c_a8d4`, corrective implementation iteration 2 (actual code/test changes responding to the two local-review findings)
- Correction: tightened candidate grounding so unlisted meaningful fact tokens and generic ungrounded strengths are rejected; added deterministic API coverage for persisted coaching replay, one semantic repair, and terminal failure without persisting fabricated output.
- Verification: Release build passed with 0 warnings/errors; Release unit tests passed (319) and integration tests passed (172); EF reports no pending model changes; changed-C# format verification, vulnerability audit and `git diff --check` are clean.
- Scope guard: no migration, quota/billing/provider change, frontend change or A9 implementation; existing A8 contract and single-call evaluation semantics remain provider-neutral.
- Next: same-task semantic re-review, then one A8 commit/PR/CI cycle.

## 2026-09-11 - A8 corrective iteration 3

- Status: Corrective implementation complete; awaiting the same-task semantic re-review
- Task/checkpoint: `c2c_a8d4`, corrective implementation iteration 3 (actual code/test changes responding to the second grounding review)
- Correction: strengths now reject novel non-generic claim vocabulary even when one answer token overlaps, while improved answers retain a narrower candidate-fact guard so faithful paraphrases remain valid; regressions cover an overlapping unsupported strength and a faithful paraphrase.
- Verification: Release build passed with 0 warnings/errors; Release unit tests passed (322) and integration tests passed (172); EF reports no pending model changes; changed-C# format verification, vulnerability audit and `git diff --check` are clean.
- Scope guard: no migration, quota/billing/provider change, frontend change or A9 implementation; no additional AI call or provider-specific behavior was introduced.
- Next: same-task semantic re-review, then one A8 commit/PR/CI cycle.

## 2026-09-11 - A8 corrective iteration 6

- Status: Corrective implementation complete; awaiting the same-task semantic re-review
- Task/checkpoint: `c2c_a8d4`, corrective implementation iteration 6 (actual code/test changes responding to the casing/position technology bypass)
- Correction: technology/entity grounding now evaluates normalized tokens regardless of casing or sentence position, recognizes technology-like suffixes and explicit integration context, and keeps faithful paraphrases outside those candidate-fact signals; the regression uses unseen lowercase `elasticsearch` without adding it to the blacklist.
- Verification: Release build passed with 0 warnings/errors; Release unit tests passed (322) and integration tests passed (172); EF reports no pending model changes; changed-C# format verification and vulnerability audit are clean.
- Scope guard: no migration, quota/billing/provider change, frontend change or A9 implementation; no additional AI call or provider-specific behavior was introduced.
- Next: same-task semantic re-review, then one A8 commit/PR/CI cycle; iteration budget is now exhausted for further corrective implementation.

## 2026-09-11 - A8 corrective iteration 5

- Status: Corrective implementation complete; awaiting the same-task semantic re-review
- Task/checkpoint: `c2c_a8d4`, corrective implementation iteration 5 (actual code/test changes responding to the sentence-start technology bypass)
- Correction: technology/entity detection now uses provider-neutral structural signals (CamelCase/acronym and technical suffix patterns) without relying on an exhaustive vendor blacklist; sentence-start technology identifiers are covered while normal prose/paraphrase remains accepted.
- Verification: Release build passed with 0 warnings/errors; Release unit tests passed (322) and integration tests passed (172); EF reports no pending model changes; changed-C# format verification and vulnerability audit are clean.
- Scope guard: no migration, quota/billing/provider change, frontend change or A9 implementation; no additional AI call or provider-specific behavior was introduced.
- Next: same-task semantic re-review, then one A8 commit/PR/CI cycle.

## 2026-09-11 - A8 corrective iteration 4

- Status: Corrective implementation complete; awaiting the same-task semantic re-review
- Task/checkpoint: `c2c_a8d4`, corrective implementation iteration 4 (actual code/test changes responding to the open-set technology finding)
- Correction: added provider-neutral detection for novel capitalized technology-like identifiers (while ignoring normal sentence/prose terms), retained candidate-specific claim protection, and added an Elasticsearch regression alongside the existing RabbitMQ and faithful-paraphrase cases.
- Verification: Release build passed with 0 warnings/errors; Release unit tests passed (322) and integration tests passed (172); EF reports no pending model changes; changed-C# format verification, vulnerability audit and `git diff --check` are clean.
- Scope guard: no migration, quota/billing/provider change, frontend change or A9 implementation; no additional AI call or provider-specific behavior was introduced.
- Next: same-task semantic re-review, then one A8 commit/PR/CI cycle.
## 2026-09-11 — B10 Skill Profile

- Status: Implementation complete / Ready for independent review
- Owner: Bảo Nguyên — Backend workstream B
- Branch: `feat/skill-profile`
- Base main: `6150717f15c563241cce24314bea7cf2cc2f7d23` (`origin/main`, including merged B9)
- Implementation commit: `de06c06b4d7ceb203f1d9f8c0601ed00da633354`
- Scope: Added the B10 computed Skill Profile read model only. The service aggregates real, validated, owner-scoped CV, interview, STAR and Scenario evidence without AI calls, Career Goal weighting, B11+ logic or changes to existing practice behavior.
- API contracts: Added authenticated `GET /api/v1/skill-profile` with the standard `{ "data": ... }` envelope. The response contains deterministic `competencies` with `code`, `name`, `category`, integer `score`, `evidenceCount`, `latestEvidenceAt` and per-source counts/timestamps, plus qualitative `weaknessSignals` for CV gaps/missing keywords.
- Evidence/business rules: Completed owner CV analyses contribute only validated mode-specific breakdown dimensions; final canonical interview reports take precedence over answer-level rubric fallback; applicable/detected valid STAR components and completed valid Scenario evaluations contribute numeric evidence. Taxonomy codes are `resume.<dimension>`, `interview.<criterion>`, `behavioral.<component>` and `scenario.<normalized-competency>`, keeping `resume.clarity` distinct from `interview.clarity`. Scores use equal-weight arithmetic means with one final `Math.Round(..., MidpointRounding.AwayFromZero)`; one valid structured numeric item is sufficient, while qualitative or malformed evidence never receives an invented score.
- Ownership/malformed data: All evidence queries are filtered by authenticated user in bounded `AsNoTracking` projection queries, covering the four required CV, interview, STAR and Scenario families. Invalid JSON or invalid evidence items are skipped individually; database failures and cancellation are not swallowed. Raw CV text, answers, STAR quotes, scenario answers and full AI payloads are not exposed.
- Files/modules: `Nexora.Business.Skills` contracts/taxonomy/aggregation, `Nexora.Data.Skills.SkillProfileService`, API DTO/controller, DI registration, focused unit/integration tests, and B10 API/data-model/frontend documentation.
- Migration impact: None. B10 is a computed read model with no entity, `DbSet`, table, ModelSnapshot change or migration; the current A4/A5/A6/A7/B9 migration chain remains untouched.
- Verification: `dotnet tool restore` and `dotnet restore Nexora.slnx` passed; Release solution build passed with 0 warnings/errors; full suite passed with 320 unit and 186 integration tests (0 failed/skipped); B10-focused coverage passed 8 unit and 17 integration tests; EF `migrations has-pending-model-changes` reported `No changes have been made to the model since the last migration.`; changed-file `dotnet format --verify-no-changes` passed; NuGet vulnerability audit returned no vulnerable package entries; CRLF checks for all changed C# blobs and `git -c core.whitespace=cr-at-eol diff --check` passed.
- FE impact: No frontend code changed. The B10 endpoint shape, owner scope, deterministic ordering, evidence traceability, empty-profile behavior and no-raw-content rule are documented for future integration.
- Dependencies/blockers: No hosted CI or Pull Request was created. Independent review remains required; B11 was not started.
- No live AI, R2, Resend, payment, production database or production service calls were made.

## 2026-09-11 - A9 Production Interview Report

- Status: Implementation complete; deterministic verification green; manual ChatGPT review pending
- Owner: Backend workstream A
- Task/branch: `A9` / `feat/a9-interview-report-production`
- Base: `00d34e5657b63439d494d353e4a593802891081a` (`main` after A8)
- Scope: Durable interview reports for two-answer partial sessions, three-answer free sessions, and longer paid sessions. Reports synthesize only answered interview questions, expose sample metadata and a partial-session disclaimer, and provide per-question reviews plus suggested improved answers projected from persisted A8 coaching.
- Failure/retry: Report processing failures leave the interview in `completing`; GET distinguishes processing, failed/retryable, and unavailable states. `POST /api/v1/interviews/{id}/report/retry` is owner-scoped, free, idempotent, and queues at most one pending report job without another interview/quota reservation.
- Durability/concurrency: The existing unique report-per-interview constraint is reused. Session row locking, short finalization transactions, pending-job checks, and duplicate-delivery handling ensure one durable report and one completed realtime transition; `completed` is persisted only with a valid report.
- AI contract: `interview.report` uses strict canonical rubric/section validation, one provider attempt, bounded output, and versioned provenance. It does not add a nested retry or a second coaching/rewrite call; persisted `interview.evaluate` coaching is reused for question-level report data.
- Files/modules: `PracticeService`, `PracticeContracts`, `InterviewsController`, OpenAPI idempotency metadata, `AiOperationCatalog`, `StructuredAiExecutor`, focused interview/report and AI validation tests, API contract documentation, and the A9 implementation checklist.
- Verification: Release build passed with 0 warnings/errors; 332 unit tests and 191 integration tests passed; focused report/AI/OpenAPI regressions passed; EF reported no pending model changes; changed-file style and analyzer verification passed; NuGet vulnerability audit reported no vulnerable packages; `git diff --check` passed. Tests use deterministic providers/SQLite and no live AI or production provider calls.
- Migration impact: None; existing interview/report persistence and migration chain are unchanged.
- Dependencies and risks: Frontend is untouched and must consume the new optional report projections. Manual independent review remains required for report grounding, transaction boundaries, duplicate-worker behavior, and failure/retry semantics; A10 was not started.
## 2026-09-11 — B11 Learning Path

- Status: Implementation complete / Ready for independent review
- Owner: Bảo Nguyên — Backend workstream B
- Branch: `feat/learning-path`
- Base main: `00d34e5657b63439d494d353e4a593802891081a` (`origin/main`, after A8, C2C retirement and CI EOL-gate updates)
- Implementation commit: `db3dbe6945407a616e17a2ac71c06e6408da3304`
- Scope: Added a persisted, normalized, deterministic Learning Path derived from the authenticated user's active Career Goal and B10 `ISkillProfileService`. Numeric gaps below 60 are critical priority, scores 60–74 are developing priority, and scores 75+ are not planned; qualitative `cv_analysis` weakness signals create supporting resume-improvement activities without invented numeric scores. Published Scenario resources are selected server-side only when a real competency match exists; no AI provider or B12+ logic was added.
- API contracts: Added authenticated `GET /api/v1/learning-path`, `POST /api/v1/learning-path`, `POST /api/v1/learning-path/refresh` and `PATCH /api/v1/learning-path/activities/{activityId}` with standard `{ "data": ... }` / `{ "error": ... }` envelopes. Generation requires an active Career Goal, is idempotent, refresh reconciles incrementally, completion supports only pending → completed, and progress is server-computed from non-obsolete activities.
- Domain/business rules: One path is maintained per user/CareerGoal pair. User-row locking, transactions and the database unique path index prevent duplicate generation; stable activity keys and reconciliation preserve completed activity IDs/completedAt, retain still-needed pending IDs, mark resolved pending activities obsolete and add new gaps as pending. Owner-scoped path/activity queries return safe not-found errors, and switching Career Goals selects a separate path without rewriting prior history.
- Files/modules: Added Business Learning contracts/planner, Data Learning entities/service, API contracts/controller, DbContext configuration, DI registration, privacy export/deletion integration, migration discovery coverage, focused unit/integration tests and B11 API/data-model/frontend documentation. `PracticeService.cs`, B10 aggregation and existing A4–A8 behavior were not modified by B11.
- Migration impact: Generated additive EF migration `20260910192103_B11LearningPath` after the existing B9/A7 chain. It creates `learning_paths`, `learning_path_milestones` and `learning_path_activities` with owner/CareerGoal restrictive FKs, cascade child FKs, owner/path/activity/milestone/resource indexes and unique `(UserId, CareerGoalId)` / `(LearningPathId, ActivityKey)` constraints. The model snapshot contains the B11 entities only in addition to the latest main model; no prior migration was rewritten.
- Verification: `dotnet restore Nexora.slnx` passed; `dotnet build Nexora.slnx --nologo --no-restore` passed with 0 warnings/errors; `dotnet test --no-build --nologo` passed with 335 unit and 212 integration tests, 0 failed/skipped; focused B11 tests passed with 5 unit and 23 integration tests; `dotnet ef migrations has-pending-model-changes --project src/Nexora.Data --startup-project src/Nexora.Api` reported `No changes have been made to the model since the last migration.`; changed-C# `dotnet format Nexora.slnx --verify-no-changes --no-restore --include ...` passed; NuGet vulnerability audit reported no vulnerable package entries; CRLF-aware `git diff --check` passed. Migration discovery coverage passed within the integration suite. No live provider or production database checks were run.
- FE impact: No frontend code changed. The Learning Path response shape, active Career Goal dependency, gap policy, statuses, progress semantics and refresh behavior are documented for future UI integration.
- Remaining blockers: No hosted CI or Pull Request was created. Independent review remains required; B12/B13 were not started.
- No live AI, R2, Resend, payment, production database or production service calls were made.

## 2026-09-11 — B11 Learning Path review corrections

- Status: Functional correction complete / Ready for re-review
- Owner: Bảo Nguyên — Backend workstream B
- Branch: `feat/learning-path`
- Base main: `00d34e5657b63439d494d353e4a593802891081a` (`origin/main`)
- Original implementation commit: `db3dbe6945407a616e17a2ac71c06e6408da3304`
- Corrective commit: `e66a0e3a74a01f81b49e620eed248f90b1943f78`
- Scope: Fixed re-emerging gaps without rewriting completed history. A completed activity remains completed with the same ID and CompletedAt; newer evidence that leaves the competency below threshold creates one deterministic pending learning-cycle activity, unchanged evidence creates no duplicate, and an existing pending cycle remains stable. Resolved pending activities still become obsolete. Scenario gaps without a published matching resource now remain visible as `external_learning` activities with the original competency code, preserved priority/milestone and null resource/link; no IDs or URLs are fabricated.
- API/data contracts: Existing Learning Path endpoints and response envelope remain unchanged. Documentation now includes `external_learning` and the newer-evidence cycle behavior; no migration or ModelSnapshot change was required.
- Verification: `dotnet restore Nexora.slnx` passed; `dotnet build Nexora.slnx --nologo --no-restore` passed with 0 warnings/errors; `dotnet test --no-build --nologo` passed with 336 unit and 213 integration tests, 0 failed/skipped; focused B11 coverage passed 6 unit and 24 integration tests; `dotnet ef migrations has-pending-model-changes --project src/Nexora.Data --startup-project src/Nexora.Api` reported `No changes have been made to the model since the last migration.`; current changed-C# `dotnet format` style and analyzer verification passed for 15 files; NuGet audit reported no known vulnerable packages; CRLF-aware `git diff --check` passed. No hosted CI or Pull Request was used for this correction.
- Migration impact: None. Existing `20260910192103_B11LearningPath` remains the sole B11 migration and the model snapshot is unchanged; the correction is deterministic Business/Data reconciliation plus tests/docs.
- FE impact: No frontend code changed. Clients should render `external_learning` without inventing a URL and retain completed activity IDs/completion timestamps across refresh responses.
- Remaining blockers: Independent re-review remains required; B12/B13 were not started.
- No live AI, R2, Resend, payment, production database or production service calls were made.

## 2026-09-11 — A10 Voice Input / STT Contract

- Status: Implementation complete / Ready for independent review
- Owner: Backend workstream A
- Task/branch: `A10` / `feat/a10-voice-input-contract`
- Base main: `bc95b35ee50c4da1b1a9b86a9687d7be1e95a604` (`origin/main`, after A9)
- Scope: Formalized the text-only voice-input boundary. Browser speech-to-text is an input aid: the user edits and confirms the transcript, then the existing answer endpoint receives only the final `content` text. Existing active-state, ownership, validation, quota, and idempotency behavior remains the canonical path for typed and voice-confirmed answers.
- Contracts and privacy: `SubmitAnswerRequest` remains `{ questionId, content, durationSeconds }`; no raw transcript, audio field, alternate answer record, or speech provider was added. Persisted answer content is the sole input for answer evaluation, follow-up context, history, and reports. `durationSeconds` remains optional timing metadata, not audio proof. Audio capture/storage and backend STT require a separately approved provider, consent, lifecycle, and retention policy.
- Files/modules: API contract documentation, security/privacy documentation, A10 implementation checklist, OpenAPI regression coverage, and end-to-end interview answer/report/idempotency regression coverage. No production runtime, persistence model, or migration changes were required because the existing endpoint already enforces the provider-neutral text contract.
- Verification: `dotnet tool restore` and `dotnet restore Nexora.slnx --nologo` passed; Release solution build passed with 0 warnings/errors; full Release unit tests passed (349) and integration tests passed (216), including the focused A10/OpenAPI regressions (2); EF `migrations has-pending-model-changes` reported no changes; changed-C# style/analyzer verification, NuGet vulnerability audit, and `git diff --check` passed. No live AI/STT/audio provider or production service calls were made.
- Migration impact: None. No EF entity, DbSet, snapshot, or migration changed.
- FE impact: No frontend code changed. FE may use browser STT but must submit the same confirmed text contract as typed input.
- Dependencies and deferred decisions: No live AI/STT/audio provider calls. Backend STT abstraction, audio upload/retention, recording consent text, and any provider selection remain deferred; A11 was not started.
