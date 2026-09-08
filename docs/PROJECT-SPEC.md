# Project Technical Specification — Nexora .NET 10 MVP

**Status:** Approved implementation baseline  
**Last updated:** 2026-09-07

## 1. Technical baseline

| Concern | Decision |
| --- | --- |
| Architecture | Modular monolith, Three-Layer: Presentation / Business / Data. |
| Runtime/language | .NET 10 LTS; C# version supplied by .NET 10 SDK unless explicitly pinned to another supported version. |
| API | ASP.NET Core 10 Web API, REST `/api/v1`. |
| Data | PostgreSQL + Entity Framework Core 10 migrations. |
| Auth | ASP.NET Core Identity with PostgreSQL EF store; Google OAuth integration. |
| Jobs | .NET worker/background jobs with persisted state, timeout and bounded retry; concrete queue package selected during implementation if needed. |
| File storage | `IStorageProvider`; development adapter allowed, production private objects + short-lived signed URLs. |
| UI hosting | Static frontend on Vercel. |
| Production hosting | Deferred under DEC-04; API has an independently configurable origin/domain. |

## 2. Module responsibilities

| Module | Business services | Owns data |
| --- | --- | --- |
| Identity | `AuthService`, `ProfileService` | users, profiles, sessions |
| Billing | `PlanService`, `CheckoutService`, `UsageService` | plans, orders, subscriptions, usage_events |
| Resume | `ResumeService`, `AnalysisService` | resumes, JDs, analyses |
| Interview | `InterviewService`, `ScoringService` | sessions, questions, answers, reports |
| Practice | `ScenarioService`, `StarService` | attempts, drafts, feedback |

## 3. Request flow example: start interview

1. `POST /api/v1/interviews` enters `InterviewController`.
2. FluentValidation validates role, CV/JD IDs and options.
3. Initial transaction validates auth, CV/JD ownership, entitlement and idempotency key; it reserves quota, creates the `starting` session and writes the outbox/background job request, then commits.
4. API returns `starting`; client polls/reloads `GET /interviews/{id}`.
5. Worker calls `IAiProvider` and validates the first usable question.
6. Worker success transaction persists the question, converts reservation to `consume` and transitions `starting → active`, then commits.
7. Terminal failure before activation instead converts reservation to `void` and transitions `starting → failed` in one transaction. Retries cannot duplicate session, question, usage event or job effect.

## 4. Data and API rules

- DTOs are separate from EF entities; never bind entity directly from HTTP request.
- Repositories only read/write data; service decides rules and transaction boundary.
- Controller returns standard errors in the API contract, never provider error/details.
- `user_id` is mandatory on personal resources and checked in the business layer.
- External integrations implement interfaces: `IAiProvider`, `IPaymentProvider`, `IStorageProvider`, `IEmailSender`.

## 5. Delivery milestones

1. **Foundation:** solution skeleton, auth, EF migrations, health checks, CI.
2. **Core entitlement:** plans, quota, checkout sandbox and signed payment webhook.
3. **Practice data:** CV upload/analysis, interview session/report, dashboard API.
4. **Hardening:** load/security tests, monitoring, backup restore, production go-live checklist.

## 6. Canonical Definition of Done reference

Áp dụng checklist chuẩn tại [05-test-strategy.md — Canonical Definition of Done](05-test-strategy.md#5-canonical-definition-of-done). Không duy trì bản checklist song song trong tài liệu này.

## 7. Vendor-neutral deployment topology cho quy mô MVP

```text
Vercel (static HTML/CSS/JS)
       | HTTPS / configured API origin
Production application host (DEC-04)
       |-- Nexora.Api (scale 1-2 instances)
       |-- Nexora.Worker (1 instance)
       |-- PostgreSQL managed database
       `-- private object storage
```

- Bắt đầu với một API instance và một worker instance; autoscale chỉ dựa trên CPU/queue depth sau khi có telemetry.
- API/worker dùng cùng database schema nhưng worker không public Internet endpoint.
- Migration chạy một lần trong pipeline release, không chạy đồng thời bởi mọi API instance.

## 8. Engineering conventions

- C# nullable enabled, `async` all I/O, cancellation token cho HTTP/job.
- Controller mỏng: bind DTO → validation → service → DTO response.
- Business service không phụ thuộc `HttpContext`, controller không gọi `DbContext` trực tiếp.
- Integration adapter đặt timeout/bounded retry cho external provider. Development defaults to `GeminiAiProvider`; optional `DeepSeekAiProvider` may be selected for local text-AI evaluation through `Ai:Provider=deepseek`, while document OCR remains Gemini. Deterministic AI test doubles, when needed, live only in the test project. `FakePaymentProvider` and `LocalStorageProvider` remain development adapters. Neither Gemini nor DeepSeek is a production selection until DEC-01 is approved.
- EF migrations là artefact source-controlled và review cùng thay đổi entity.

## 9. Provider decision boundary

DEC-01–04 không block development, Phases 0–3 hoặc integration tests. Chúng chỉ block production AI, real Vietnamese payments/refunds, affected production data processing/legal enablement, và production infrastructure deployment tương ứng. Canonical scope nằm tại [07-architecture-decisions.md](07-architecture-decisions.md#production-enablement-decisions-dec-01-through-dec-04).
