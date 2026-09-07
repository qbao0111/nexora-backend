# Nexora Implementation Specification

**Status:** Approved implementation baseline  
**Baseline:** Frozen for Phase 0 backend implementation  
**Last updated:** 2026-09-08

This is the concise implementation source of truth. Formal requirements and priorities remain in [SRS](docs/SRS.md); detailed contracts remain in the linked specifications.

## 1. Product

Nexora is a private AI interview-practice and coaching web application. Its primary loop is:

```text
CV/JD → personalised mock interview → rubric/evidence feedback → report → further practice
```

AI output is coaching guidance, not hiring truth. Nexora must not fabricate achievements or position itself as covert assistance during real interviews.

## 2. MVP scope

Production MVP **Must** work concentrates on:

- Account/authentication, profile essentials and owner isolation.
- CV/JD private persistence, validation, extraction boundary and analysis.
- Text mock interviews, durable questions/official answers and resumable history.
- Validated AI evaluation and evidence/rubric report.
- Server-owned plans, entitlements, transactional quota and auditable usage.
- Pending order → authenticated, idempotent webhook → paid → entitlement boundary, proven with a fake/sandbox provider before a real provider is chosen.
- Basic dashboard/history needed to continue the main journey.
- Observability, privacy/delete/export, recovery and production security gates.

STAR drafts/feedback and scenario practice remain **Should** requirements. They may ship with the MVP when capacity permits, but do not silently become launch blockers. The full Must/Should/Could list is canonical in [SRS §6](docs/SRS.md#6-functional-requirements).

## 3. Non-goals

- Covert live-interview copilot or answer assistance.
- Audio/video or speech metrics in the core MVP; any future recording requires explicit consent.
- Native mobile apps, recruiter ATS/workspaces, job marketplace or automated applications.
- Microservices, Kubernetes, event-streaming platforms, CQRS/event sourcing or speculative scaling architecture.

## 4. Technical baseline

| Concern | Fixed decision |
| --- | --- |
| Runtime | .NET 10 LTS |
| API | ASP.NET Core 10 Web API, REST JSON `/api/v1` |
| Language | C# version supplied by the .NET 10 SDK unless an explicitly supported version is pinned |
| Persistence | Entity Framework Core 10 + PostgreSQL |
| Identity | ASP.NET Core Identity with PostgreSQL EF store |
| Frontend | Existing static HTML/CSS/JavaScript on Vercel |
| Long work | .NET worker/background jobs |

Do not pin architectural documents to arbitrary servicing patch versions. Project/package files may use the latest supported .NET 10 servicing release.

## 5. System topology

```text
Browser → static Vercel frontend → Nexora.Api
                                  ├─ Nexora.Business → Nexora.Data → PostgreSQL
                                  ├─ Nexora.Integrations → storage/payment/email
                                  └─ job/outbox → Nexora.Worker → AI/extraction adapters
```

This is one modular monolith, one logical PostgreSQL database and a separately runnable worker, not microservices. Detailed topology: [architecture](docs/02-architecture.md).

## 6. Modules

```text
src/
  Nexora.Api/             Presentation, middleware, auth, DTO mapping
  Nexora.Business/        Services, policies, validation, provider interfaces
  Nexora.Data/            DbContext, repositories, migrations
  Nexora.Integrations/    AI, payment, storage and email adapters
  Nexora.Worker/          Bounded background job execution
tests/
  Nexora.UnitTests/
  Nexora.IntegrationTests/
```

Business modules are Identity/Profile, Billing/Entitlement, Resume/JD, Interview/Report, and optional-priority Practice. Controllers bind/validate/map and delegate. Business services own rules and transaction orchestration. Repositories persist/query only. DTOs and provider types never become EF entities or leak across boundaries.

## 7. Core domain model

- ASP.NET Core Identity is the credential/role source of truth; `ApplicationUser`/profile extend it.
- User-owned Resume, JobDescription, Analysis, InterviewSession, Answer, Report, StarDraft and ScenarioAttempt records carry owner identity and timestamps.
- Billing uses versioned Plan/Price, Order, PaymentEvent, Subscription/Entitlement and immutable UsageEvent records.
- Files persist private `storage_key`, checksum, detected MIME, size and processing state—not public URLs.
- Idempotency, outbox and audit records support reliable mutations and jobs.

Physical constraints and lifecycle ownership: [data model](docs/08-data-model.md).

## 8. Core API

- Base URL is `/api/v1`; timestamps use UTC ISO-8601.
- Client inputs never control user identity, price, plan, quota, entitlement or score.
- Core routes cover `/me`, `/plans`, checkout/payment webhooks, upload/resume/analysis, interviews/answers/completion/report, and dashboard/history.
- User resources always require owner authorization; cross-user lookup should not leak metadata.
- Response DTOs are allow-listed; EF/provider objects are never serialized directly.

Canonical endpoints and payloads: [API/data contract](docs/03-api-data-contract.md).

## 9. Authentication and authorization

ASP.NET Core Identity owns credentials, roles, reset/verification and revocation data. The fixed transport is a short-lived access token held in browser memory and sent as `Authorization: Bearer`, with refresh-token rotation through a `HttpOnly`, `Secure`, appropriately `SameSite` cookie. Cookie endpoints require CSRF controls; deployments require HTTPS and CORS allow-lists. Never store refresh tokens in `localStorage`.

Backend policies are authoritative for every operation, including ownership and admin actions. Admin role does not imply routine access to raw CV/transcript content. See [ADR-002/003](docs/07-architecture-decisions.md) and [security/privacy](docs/06-security-privacy.md).

## 10. Interview lifecycle

The one canonical state machine is:

```text
draft → starting → active → completing → completed
starting → failed
active → abandoned
```

- `starting`, `completing` and job states expose asynchronous progress.
- Only `active` accepts an official answer.
- An official question accepts one official answer unless a separate revision contract is later approved.
- Completion happens exactly once; report creation is idempotent.
- Optimistic concurrency/versioning prevents duplicate answers and transitions.
- `completed`, `failed` and `abandoned` are immutable terminal states except explicit audited administrative/recovery procedures.

`docs/08-data-model.md` owns the canonical persisted interview state names and transitions, subject to the formal behavior and business rules in `docs/SRS.md`; API and design documents reference them.

## 11. AI integration contract

```csharp
public interface IAiProvider
{
    Task<T> GenerateStructuredAsync<T>(
        AiRequest request,
        CancellationToken cancellationToken);
}
```

`AiRequest` is Nexora-owned and carries purpose, prompt/model/rubric/schema versions, bounded untrusted input, output schema, token budget and correlation metadata. Every result receives JSON schema and semantic validation before persistence. Provider failures map to stable internal categories and safe API errors.

Development defaults to `GeminiAiProvider` through the `IAiProvider` boundary. The optional `DeepSeekAiProvider` can be selected with `Ai:Provider=deepseek` for local text-AI evaluation; `IDocumentOcrProvider` remains Gemini regardless of that selector. Deterministic AI test doubles, when needed, stay inside the test project. Gemini and DeepSeek remain internal-development integrations unless DEC-01 later selects a production provider: provider SDK/HTTP types stay in `Nexora.Integrations`, model IDs come from configuration, keys come from secret configuration, and outputs map to Nexora schemas. Details: [AI integration specification](docs/09-ai-integration-spec.md).

The structured executor remains bounded to two provider calls. Only a positively detected DeepSeek reasoning-budget exhaustion may select one per-attempt `low` override; semantic repair remains on the configured policy and `RepairUsed` continues to mean semantic repair. See the [AI integration specification](docs/09-ai-integration-spec.md#221-confirmed-reasoning-budget-fallback) for detection and telemetry rules.

## 12. Billing and quota model

Plans/prices are server-owned and snapshotted on orders. Before DEC-02, use `IPaymentProvider` with `FakePaymentProvider` to exercise:

```text
pending order → verified simulated webhook → paid → entitlement fulfillment
```

Duplicate webhook events produce one payment record and one fulfillment. A real Vietnamese provider is deferred.

Interview quota uses immutable actions:

```text
reserve → consume
reserve → void
adjustment (audited correction/credit; never history editing)
```

- The initial API transaction validates authentication, optional CV/JD ownership, entitlement and `Idempotency-Key`, then atomically reserves quota, creates the `starting` session and records the outbox/job request.
- On worker success, persist the first usable question, convert the reservation to `consume`, and transition `starting → active` as one coherent PostgreSQL transaction wherever feasible.
- On terminal worker failure before the question is persisted and session becomes `active`, convert the reservation to `void` and transition `starting → failed` in one transaction.
- Once `active`, disconnect, refresh, navigation away, no answer, or later AI/report failure does not automatically void consumed usage.
- Report retry after consumption is free and idempotent.
- Terminal report failure creates the documented automatic credit adjustment or audited support process.
- Abandon/disconnect does not automatically credit usage.

[SRS BR-03/04/07/08](docs/SRS.md#7-business-rules) and [ADR-005](docs/07-architecture-decisions.md) are canonical.

## 13. File/storage model

Business/API code uses `IStorageProvider`. Before production selection, use `LocalStorageProvider` or another development adapter. Local filesystem storage is not production-suitable.

Production requires private objects, authorization before upload/download access, short-lived signed URLs where supported, stored keys rather than public URLs, and size/extension/detected MIME/signature checks. Malware scanning is an optional defence when available, never a claimed guarantee. See [storage ADR](docs/07-architecture-decisions.md) and [security](docs/06-security-privacy.md).

## 14. Background jobs

Document extraction, CV analysis and report generation are asynchronous. Question generation may be synchronous only within an explicit timeout budget; interview lifecycle remains observable. Jobs require a stable identity/correlation ID, idempotent handling, persisted state, timeout, bounded retry/backoff and visible terminal failure. A retry cannot duplicate usage, answers, transitions or reports.

## 15. Error/idempotency conventions

Standard error envelope:

```json
{
  "error": {
    "code": "ERROR_CODE",
    "message": "Safe user-facing message",
    "requestId": "..."
  }
}
```

Require `Idempotency-Key` when duplicate execution is unsafe: checkout/order creation, chargeable analysis/job creation, interview start, official-answer submission, interview completion/report trigger, applicable administrative mutations, and externally triggered processing without a stronger provider event identity. The same actor + operation + key + equivalent payload returns the original result; reuse with a different payload returns `409 IDEMPOTENCY_CONFLICT`. Do not require the key on reads.

## 16. Security/privacy invariants

- Test BOLA/object and function authorization negatively for every resource/action.
- Keep objects private and validate file size, format and content signature.
- Verify webhook signatures/timestamps and unique event IDs before state changes.
- Keep secrets in secret configuration; redact logs and never record raw CV/transcript/answer content by default.
- Treat CV/JD/answers as prompt-injection input; delimit and constrain it without tools or secrets.
- Support core data export and account/data deletion according to the finalized retention policy.
- Obtain explicit consent before any future audio/video capture.

Final retention periods and legal text remain DEC-03. Engineering must keep these policies configurable and testable. Full threat model: [security/privacy](docs/06-security-privacy.md).

## 17. Testing/release gates

[Test strategy](docs/05-test-strategy.md) exclusively owns the canonical Definition of Done and T-01 through T-10 production-risk scenarios. Relevant unit/integration/contract/E2E and operational evidence must pass. When projects exist, the minimum local commands are:

```text
dotnet restore
dotnet build
dotnet test
```

Implementation is incomplete while relevant tests fail. Production also requires the [runbook go-live gates](docs/04-production-runbook.md).

## 18. Deferred provider decisions

| Decision | Still unresolved | Effect |
| --- | --- | --- |
| DEC-01 | Production AI provider/model and per-user/global production budgets/controls | Blocks real production AI enablement only |
| DEC-02 | Vietnamese payment provider; refund, invoice and tax handling | Blocks real production payment/refund enablement only |
| DEC-03 | Final retention periods and approved Terms/Privacy/AI/recording text | Blocks production processing/go-live where policy disclosure is required |
| DEC-04 | Production hosting/storage vendors, domains, mail provider and infrastructure accounts | Blocks deployment of affected production infrastructure only |

None blocks backend/local development, Phases 0–3, or integration testing with fake/development adapters. Do not infer a final choice from examples or development implementations.

## 19. Delivery order

1. **Phase 0 — readiness:** documentation freeze, solution/projects, CI, dev configuration/secrets, PostgreSQL and OpenAPI baseline.
2. **Phase 1 — foundation:** Identity/auth/profile, migrations, error/correlation conventions, authorization and storage abstraction.
3. **Phase 2 — billing/entitlement:** plan, entitlement, ledger/quota transaction, orders and fake payment/webhook path.
4. **Phase 3 — core AI practice:** CV/JD, interview state machine, Gemini default adapter plus the optional DeepSeek local text adapter, questions/answers/report, then STAR/scenario according to SRS priority.
5. **Phase 4 — production integration/hardening:** resolve DEC-01–04, integrate selected providers/infrastructure, legal/retention, monitoring, security, recovery, load tests and deploy.

Full phase exits: [delivery plan](docs/10-delivery-plan.md).

## 20. Source-of-truth document map

| Document | Ownership |
| --- | --- |
| [README.md](README.md) | Entry point, status and navigation |
| [AGENTS.md](AGENTS.md) | Persistent coding-agent instructions |
| `SPEC.md` | Concise implementation source of truth |
| [00-market-research.md](docs/00-market-research.md) | Product research and positioning |
| [01-requirements.md](docs/01-requirements.md) | Product PRD and priorities |
| [SRS.md](docs/SRS.md) | Formal product/system behavior, requirements, priorities and business rules |
| [PROJECT-SPEC.md](docs/PROJECT-SPEC.md) | Detailed technical specification |
| [02-architecture.md](docs/02-architecture.md) | Architecture and design patterns |
| [03-api-data-contract.md](docs/03-api-data-contract.md) | HTTP routes, request/response, error and idempotency contracts |
| [04-production-runbook.md](docs/04-production-runbook.md) | Deployment and operations |
| [05-test-strategy.md](docs/05-test-strategy.md) | Test strategy and the canonical Definition of Done |
| [06-security-privacy.md](docs/06-security-privacy.md) | Threat model and privacy |
| [07-architecture-decisions.md](docs/07-architecture-decisions.md) | Approved architecture choices/rationale and deferred decision gates |
| [08-data-model.md](docs/08-data-model.md) | Persisted entities/schema/state representations, subject to SRS rules |
| [09-ai-integration-spec.md](docs/09-ai-integration-spec.md) | AI contract, rubric and job semantics |
| [10-delivery-plan.md](docs/10-delivery-plan.md) | Implementation phases |
| [11-analysis-design-models.md](docs/11-analysis-design-models.md) | Use cases, diagrams and traceability |
