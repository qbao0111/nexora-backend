# Nexora Agent Instructions

**Status:** Approved implementation baseline  
**Last updated:** 2026-08-21

## Project identity

Nexora is an AI-powered interview-practice and coaching product. The core journey is CV/JD → personalised mock interview → rubric/evidence-based feedback → report → further practice. The MVP is not live-interview cheating, covert assistance or copilot software.

## Mandatory technical baseline

- .NET 10 LTS; ASP.NET Core 10; Entity Framework Core 10; PostgreSQL.
- ASP.NET Core Identity owns credentials, roles and Identity tokens.
- REST JSON API base path: `/api/v1`.
- Use the C# version supplied by the .NET 10 SDK unless the repository explicitly pins another supported language version.

## Architecture rules

- Build a modular monolith using Presentation → Business → Data.
- Use `Nexora.Api`, `Nexora.Business`, `Nexora.Data`, `Nexora.Integrations`, `Nexora.Worker`; tests live in `Nexora.UnitTests` and `Nexora.IntegrationTests`.
- Keep controllers thin. Services/policies own business rules; repositories/data access do not decide them.
- DTOs are not EF entities. Controllers must not use `DbContext` directly.
- Access AI, payment, storage and email only through adapters. Controllers and frontend must never call providers directly.
- Do not introduce microservices without an explicit new ADR. Do not add Domain/Application/Infrastructure projects merely to imitate Clean Architecture.

## Security invariants

- Backend authentication and authorization are authoritative. Never trust client-supplied `userId`, price, plan, quota or score.
- Authorize ownership for every user-owned resource; opaque IDs do not replace authorization.
- Secrets never enter frontend code, source control or logs.
- Keep files private; persist storage keys, authorize before download, and use short-lived signed URLs where supported.
- Treat CV, JD and answers as untrusted AI input.
- Authenticate payment webhooks and process them idempotently.

## Reliability invariants

- Require idempotency for unsafe mutation/job creation operations as specified in `docs/03-api-data-contract.md`.
- Make quota operations transactional. Usage events are immutable and auditable: `reserve`, `consume`, `void`, `adjustment`.
- Enforce state machines and reject invalid transitions. Terminal interview states are immutable except explicit audited administrative/recovery processes.
- Normalize external-provider failures. Jobs require timeouts, bounded retries, correlation/telemetry and an observable terminal failure.

## AI rules

- Access AI only through `IAiProvider`; provider SDK types must not leak into Business or API contracts.
- Validate structured output against both a schema and Nexora semantic rules.
- Version prompts, models, rubrics and result schemas.
- Never fabricate candidate achievements. Feedback claims should cite answer evidence where possible.

## Testing rules

`docs/05-test-strategy.md` owns the canonical test strategy and Definition of Done. When corresponding projects exist, run at least:

```text
dotnet restore
dotnet build
dotnet test
```

Implementation is not complete while relevant tests fail.

- Automated unit and integration tests use deterministic fake providers by default.
- Normal local test runs must not depend on live Gemini, payment providers or external network availability.
- Live provider contract/sandbox tests must be separately identifiable and excluded from normal local unit/integration runs.

## EF Core migrations

- EF Core migrations are source-controlled artifacts. Do not use `Database.EnsureCreated()` for the normal production application path.
- Never rewrite an already-applied production migration to alter schema history; create a new migration.
- Destructive schema changes require explicit migration and data-retention consideration.
- Production migrations run once through the release/deployment process, not concurrently from every API/worker instance.

## Documentation ownership

- `SPEC.md`: concise implementation source of truth.
- `docs/SRS.md`: formal product/system behavior, testable requirements, priorities and business rules.
- `docs/07-architecture-decisions.md`: approved architecture choices/rationale and deferred production decision gates.
- `docs/08-data-model.md`: persisted entities/schema/state representations, subject to SRS rules.
- `docs/03-api-data-contract.md`: HTTP routes, request/response, error and idempotency contracts.
- `docs/05-test-strategy.md`: canonical Definition of Done.
- Detailed documents expand these sources but must not contradict them.

## Specification integrity and scope

- Do not change requirements, ADRs, security invariants, public contracts or test expectations merely to ease implementation or make failing code pass.
- When implementation conflicts with an approved specification, conform the implementation unless the task explicitly requests an approved specification/decision change.
- Never silently downgrade a Must requirement to Should/Could.
- Do not refactor unrelated modules, rename public contracts, introduce new abstractions or change project structure unless the current task requires it.
- Prefer the smallest coherent implementation that satisfies the linked SRS requirement and tests.

## Required agent workflow

1. Read `AGENTS.md`.
2. Read `SPEC.md`.
3. Locate related SRS IDs.
4. Inspect the relevant detailed documents.
5. Inspect existing implementation before changing code.
6. Make the smallest coherent change.
7. Add or update tests.
8. Update documentation only when a contract or decision actually changes.

DEC-01 through DEC-04 are deferred production choices, not engineering blockers. Use the documented fake/development adapters and never invent a final provider, budget, retention/legal policy, host, domain or infrastructure account.
