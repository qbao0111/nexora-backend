# Backend Delivery Plan — Nexora .NET 10

**Status:** Approved implementation baseline  
**Last updated:** 2026-08-21

DEC-01–04 are production enablement gates, not prerequisites for Phases 0–3. Fake/development adapters are the required path until production-specific decisions are approved.

## Phase 0 — Repository and engineering readiness

- Complete documentation freeze and create `.sln`.
- Create the five source projects and two test projects in the approved structure.
- Establish CI, development environment and configuration/secret structure.
- Provision development PostgreSQL and define EF Core migration workflow.
- Add OpenAPI baseline and repository engineering conventions.

**Exit:** .NET 10 solution restores/builds/tests; CI and local configuration work; Phase 1 tickets link SRS IDs. Production vendors are not required.

## Phase 1 — Foundation

- ASP.NET Core Identity, email/password and Google OAuth boundary.
- Profile, EF migrations and persistence conventions.
- Standard error envelope, correlation ID and health endpoints.
- Ownership/admin authorization policies and negative tests.
- `IStorageProvider` with `LocalStorageProvider` or development adapter; production private-storage contract remains enforced.
- Replace only auth/profile localStorage paths behind a feature flag.

**Exit:** T-01/T-02 and login E2E pass; API follows `/api/v1` and auth transport ADR.

## Phase 2 — Billing and entitlement domain

- Server-owned plan catalogue and price snapshots.
- Subscription/entitlement model and immutable usage ledger.
- Transactional `reserve`, `consume`, `void`, `adjustment` quota flow.
- Orders, payment events and outbox/idempotency records.
- `IPaymentProvider` + `FakePaymentProvider` covering pending order → verified simulated webhook → paid → one entitlement, including duplicate delivery.

**Exit:** T-03/T-04/T-05 pass; no client-controlled price/quota; fake webhook path is integration-tested. Real provider may follow after DEC-02.

## Phase 3 — Core AI practice

- CV/JD private persistence and extraction boundary.
- Canonical interview lifecycle `draft → starting → active → completing → completed`, plus `starting → failed` and `active → abandoned`.
- `IAiProvider` with `FakeAiProvider` first.
- `GeminiAiProvider` as configuration-driven development/testing adapter only.
- Durable question/official-answer flow, validated evaluation and idempotent evidence/rubric report.
- Basic dashboard/history for the main journey.
- STAR/scenario persistence only according to SRS Should priority and available capacity.
- Migrate relevant static frontend paths to `fetch` API with loading/error/retry states.

**Exit:** T-06/T-07/T-08 pass in integration/staging using fake/dev providers; official answer, state and report idempotency are proven.

## Phase 4 — Production integration and hardening

- Resolve DEC-01 and integrate/enable approved production AI provider/budgets.
- Resolve DEC-02 and integrate approved production payment/refund handling.
- Resolve DEC-04 production storage/hosting/domains/mail/infrastructure.
- Resolve DEC-03 final retention and approved legal/privacy/consent text.
- Complete monitoring/alerts, security review, rate limits, data export/delete, backup/restore, load tests and deployment.

**Exit:** T-09/T-10, canonical [test/release gates](05-test-strategy.md) and [go-live checklist](04-production-runbook.md#checklist-go-live) pass. All production capabilities have their required DEC approval.

## Frontend migration rules

1. Add a small `js/api-client.js`; one place handles base URL, auth retry and error mapping.
2. Replace localStorage one feature at a time; never run local and server quota as two sources of truth.
3. Keep guest preview static; server returns `401` only for protected mutations.
4. Release behind feature flags and remove mock data only after the server path is verified.

## Definition of Ready for a backend ticket

- Linked SRS ID and acceptance tests.
- Request/response contract, authorization rule and state transition stated.
- Data migration/retention impact known.
- AI/payment/storage external dependency and failure behavior defined without inventing a deferred production choice.

## Definition of Done reference

The only canonical checklist is [05-test-strategy.md — Canonical Definition of Done](05-test-strategy.md#5-canonical-definition-of-done). Definition of Ready above is not a competing DoD.
