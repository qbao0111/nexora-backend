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

## C2C phase model

The global C2C policy defines the mechanics; this section applies them to Nexora.
Keep these concepts separate:

- **Implementation iteration:** one substantive implementation or corrective change to product code, tests, configuration or task-owned documentation. Waiting, rereading unchanged files, capturing evidence and resuming a review do not consume an iteration.
- **Semantic review:** an independent ChatGPT review of requirements, architecture, security, behavior, tests and scope. Run one local review after deterministic validation and one final remote review after hosted CI is green for the exact pushed `HEAD`.
- **Evidence refresh:** a metadata-only read of the current branch/PR, base, diff and checks, or an execution-record update. Evidence refresh resumes the existing review and is not a new implementation iteration or semantic review. A changed `HEAD`/base/diff, adverse check, scope drift or new blocker invalidates the prior approval and requires fresh exact-head review.

Codex owns deterministic and mechanical failures before asking ChatGPT for semantic review: compilation, tests, formatting, EF verification, vulnerability scans, CI syntax and repository hygiene. Escalate only when the failure exposes a genuine contract or architectural decision.

## Bounded handoff and recovery

- `STATE: EXECUTED` is a durable checkpoint for the current task and implementation iteration. Once sent, do not re-execute the implementation because ChatGPT is pending, the browser is generating, or a review wait timed out.
- A semantic review has a finite budget and must end with `accepted`, `changes_required`, or `REVIEW_DEFERRED` plus an explicit reason. `CHATGPT_RESPONSE: TIMEOUT`/`REVIEW_DEFERRED` is a control-plane result, not an implementation failure or a new iteration.
- Codex relies on protocol state and the persisted C2C checkpoint, never on a UI spinner such as “Reviewing Workspace Changes”, to decide whether a turn has ended. Keep the checkpoint intact after a bounded timeout and return control so the same task can resume.
- A late ChatGPT verdict remains attached to the same task/checkpoint when applicable. Do not create a duplicate task or `STATE: EXECUTED`, and do not increment the implementation iteration unless repository content changes in response to a concrete finding.
- On timeout recovery, re-read `workspace_info`, `git_status`, execution summary/test evidence and exact `HEAD`/diff identity. If they are unchanged, continue at review/commit/remote validation; do not rerun implementation or unaffected gates.
- Reuse exact checkpoint evidence while the source diff and gate inputs are unchanged. Rerun only checks invalidated by a subsequent change or required by repository policy.

## Connector health and failover

- `workspace_info` is the first connector health check. Confirm it names `NexoraBackend` before reading files or asking ChatGPT to review.
- Prefer the healthy workspace connector `Codex with ChatGPT · NexoraBackend`. If a connector returns an account/authentication/connectivity 4xx (for example, `NexoraBackend-Laptop` cannot connect the account), retry that connector at most once when a transient cause is plausible, then fail over to another configured connector only after confirming it resolves to the same workspace.
- Never loop on a failing connector, silently switch to another workspace, expose credentials, or create a duplicate C2C task. Record the result and preserve the existing checkpoint.

## Execution progress watchdog

- Every implementation or corrective phase must have observable bounded progress: a workspace mutation, a completed tool/action, command or test output, or an explicit persisted checkpoint/state transition. A UI label such as "Implementing..." is not evidence of progress.
- If a phase produces no observable progress for a bounded inactivity window, classify it as `EXECUTION_STALLED`. This is a control-plane state, not an implementation failure or a new iteration. Preserve the dirty workspace and current task/checkpoint, return control, and recover by reconciling `workspace_info`, `git_status`, the `HEAD` diff and execution evidence.
- Individual long-running commands also require bounded timeout handling with the command identity and status recorded. Never wait on hidden reasoning or UI activity as proof that work continues, and never replay already-applied edits after a stall.

The repository profile allows at most six genuine implementation/corrective iterations per task (`.c2c.json`). Fail closed for semantic corrections that exhaust the budget; never bypass review by relabelling an evidence refresh.

Before local or remote semantic review, run the same applicable deterministic gates used by Backend CI, including the changed-file formatter. After hosted CI is green, gather one compact exact-head evidence record and request the final remote review. Do not repeat a semantic review for unchanged code merely because evidence was refreshed.

`project_log.md` records durable implementation facts and verification outcomes. Do not create a metadata-only PR-head/CI churn loop: volatile exact SHAs, mergeability and review state belong in C2C execution records and remote evidence. An in-progress entry may use `pending` for a not-yet-created commit or PR; never invent either value.

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
- The final handoff must state changed files, contract/decision impact, tests and remote evidence, remaining risks, rollback considerations, and whether the next dependency is ready.

## Conditional merge gate

Nexora opts into conditional auto-merge, but only for a task that explicitly authorizes it. Codex may merge a pull request automatically only when all of the following are true:

1. The current task explicitly permits auto-merge.
2. Independent ChatGPT review returns exactly `STATE: DONE`, `VERDICT: READY_TO_MERGE`, the PR number, the approved exact `HEAD`, and `CI: GREEN`.
3. Immediately before merging, Codex verifies that the PR is open, not draft and mergeable; the current remote head exactly matches the approved head; required hosted checks for that exact head are green; no commit appeared after review; the remote base/main has not changed in a meaningful unreviewed way; no unresolved blocker or review finding exists; and the PR scope still matches the task.
4. The normal repository merge method succeeds without an admin or bypass override.

If any evidence changes, do not merge. Refresh the remote evidence and obtain another independent ChatGPT remote review. This policy never authorizes production deployment, CI bypass, unsafe merges or a global auto-merge default; the human project owner may disable conditional auto-merge at any time.
