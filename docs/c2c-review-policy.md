# Nexora C2C Review Policy

This repository uses the global C2C review policy plus the Nexora-specific rules below.

## Preflight and ownership

- Read `AGENTS.md`, `SPEC.md`, the related requirement IDs in `docs/SRS.md`, the owning detailed documents, `implementation_plan.md`, and the newest factual entries in `project_log.md` before editing.
- Inspect the actual branch, `HEAD`, worktree, recent history, related branches and pull requests. Preserve unrelated user changes.
- A dependent workstream may start only after `project_log.md` records the prerequisite as completed with a concrete commit or merged pull request. Keep one coherent capability per branch/PR and do not silently take over a hot file owned by another workstream.
- Use the smallest change that satisfies the approved contract. Do not invent a provider, production vendor, feature, migration, or architecture change that remains deferred.

## Fixed technical baseline

- Application runtime: .NET 10 LTS, ASP.NET Core 10, Entity Framework Core 10, and the C# version supplied by the .NET 10 SDK unless explicitly pinned.
- PostgreSQL is the relational store; ASP.NET Core Identity owns credentials, roles, and identity tokens.
- The backend is a modular monolith with `Nexora.Api`, `Nexora.Business`, `Nexora.Data`, `Nexora.Integrations`, and `Nexora.Worker`, plus unit and integration test projects.
- Keep the direction Presentation → Business → Data. Integrations are reached through provider-neutral adapters. Controllers stay thin, DTOs are not EF entities, and controllers never use `DbContext` directly.

## Product and provider boundaries

- Nexora is a practice/coaching product, not live-interview cheating or copilot software. Do not add live copilot, audio/video, mobile, recruiter ATS/workspace, or marketplace scope without an approved requirement/ADR.
- `DEC-01` (production AI/model/budget), `DEC-02` (production Vietnamese payment/refunds/tax), `DEC-03` (production retention/legal text), and `DEC-04` (production hosting/domains/mail/infrastructure) are deferred production decisions. They do not block local development, fake/dev providers, or integration tests; they do block corresponding production enablement.
- Keep Business/API contracts provider-neutral. Fake/development AI, payment, storage, and OCR adapters are allowed before production vendor selection. Never claim Gemini, DeepSeek, a fake provider, or local storage is the final production choice.
- Do not change quota, billing, interview, CV, STAR, scenario, or ProductionSafety semantics while working on an unrelated capability.

## Invariants to protect

- Backend authorization is authoritative. Enforce ownership for every user-owned object; never trust client `userId`, price, plan, quota, or score.
- Keep private files behind storage abstractions with storage keys, authorization before download, and short-lived signed URLs where supported. Never log secrets, raw CV/JD/transcript content, prompts, reasoning, provider responses, or signed URLs.
- Preserve schema plus semantic AI validation, versioned prompts/models/rubrics, grounded evidence, and the rule that candidate achievements are never fabricated. Provider retries must stay bounded and must not silently consume quota twice.
- Preserve interview lifecycle and report semantics: `draft → starting → active → completing → completed`, with `starting → failed` and `active → abandoned`; only `active` accepts official answers, terminal states are immutable except audited recovery, completion is exactly once, and report generation is idempotent.
- Preserve quota events `reserve`, `consume`, `void`, and `adjustment` as immutable audit history. State transitions reject invalid/concurrent duplicates.
- Preserve authenticated/idempotent payment webhook handling, BOLA protections, upload MIME/signature/size validation, deletion/export capability, and explicit future consent for audio/video.

## Verification and handoff

- Run the relevant project tests plus `dotnet restore`, `dotnet build`, and `dotnet test` when those projects exist. Include `dotnet ef migrations has-pending-model-changes` for data-affecting work and `git diff --check` for every change.
- Do not report implementation complete while relevant tests fail. Normal tests use deterministic fake providers and must not need live AI, payment, storage, Neon, or production secrets.
- Update `project_log.md` in the same PR as completed work using the repository template: date, status, owner, branch, commit/PR, scope, traceability, files/modules, exact verification, dependencies, and remaining blockers. Record outcomes, never secrets or unverified claims.
- The final handoff must state changed files, contract/decision impact, tests and remote evidence, remaining risks, rollback considerations, and whether the next dependency is ready. Human review owns the merge and production decision.
