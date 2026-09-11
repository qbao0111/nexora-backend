# Data Model Specification — Nexora

**Status:** Approved implementation baseline; retention values deferred  
**Last updated:** 2026-09-12

## 1. Aggregates và ownership

```text
ApplicationUser 1--N Resume 1--N ResumeAnalysis
ApplicationUser 1--N InterviewSession 1--N InterviewQuestion 1--1 InterviewAnswer
InterviewQuestion 1--N InterviewQuestion (ParentQuestion -> FollowUps)
InterviewSession 1--1 InterviewReport
ApplicationUser 1--N StarDraft / ScenarioAttempt
ApplicationUser 1--N Order 1--N PaymentEvent
ApplicationUser 1--N Subscription 1--N Entitlement 1--N UsageEvent
```

`Case` và `Plan` là catalog do admin/system sở hữu; `ScenarioAttempt`/`CaseProgress` thuộc candidate.

## 2. Bảng cốt lõi

| Table | Mục đích | Constraint quan trọng |
| --- | --- | --- |
| `asp_net_users`, `user_profiles` | Identity và profile | Identity là source of truth credential. |
| `plans`, `plan_prices` | Catalog/version giá | Không sửa price đã được order tham chiếu. |
| `orders`, `payment_events` | Payment lifecycle | unique provider event/transaction ID. |
| `subscriptions`, `entitlements` | Quyền theo thời hạn | Có `starts_at`, `ends_at`, `status`, snapshot. |
| `usage_events` | Ledger reserve/consume/void/adjustment quota | immutable, unique idempotency key. |
| `resumes`, `stored_files` | CV file + extracted text | `storage_key` private; checksum, MIME, scan/extract state. |
| `upload_intents` | Durable browser-upload capability state | owner-scoped token hash, private storage key, expected/actual size, expiry, checksum and finalized timestamp; unique token/storage-key constraints. |
| `job_descriptions`, `resume_analyses` | JD và output analysis | input snapshot/model/prompt version. |
| `interview_sessions`, `interview_questions`, `interview_answers`, `interview_reports` | Practice loop | answer unique per official question, session state machine. `kind` is `primary` or `followup`; `topic` is explicit and `parent_question_id` links a follow-up to its parent. |
| `star_drafts`, `scenario_attempts` | Practice support | owner ID, version/status. |
| `idempotency_keys`, `outbox_events`, `audit_logs` | Reliability/operations | expiry/retention job. |
| `data_privacy_requests` | Audit/retry state cho export/delete workflow | unique `(user_id, idempotency_key)`; không FK cascade để audit còn lại sau anonymization. |
| `realtime_notifications` | Minimal owner-targeted resource-change delivery metadata | `Id`, `UserId`, `ResourceType`, `ResourceId`, `Status`, `CreatedAt`, nullable `ProcessedAt`/`NextAttemptAt`, `Attempts`; pending index `(ProcessedAt, CreatedAt)`. Inserted with resource transition; see [delivery contract](realtime-notifications.md). |

## 3. Required columns

All user-owned records: `id UUID/ULID`, `user_id`, `created_at timestamptz`, `updated_at timestamptz`; add `(user_id, created_at DESC)` index for user history. Soft-delete only where recovery/retention requires it; otherwise hard-delete file content after legal retention period.

### Resume analysis v2 persistence

`resume_analyses` stores the explicit `Mode` (`job_targeted` or `field_benchmark`) and a JSON `ContextJson` snapshot. `JobDescriptionId` and `JobDescriptionVersion` are nullable only for `field_benchmark`; existing rows are backfilled as `job_targeted` by the CV Analysis v2 migration and database check constraints enforce the legal mode/JD pairs. Each row also stores the resume version, analysis model/prompt/schema/rubric versions, and a private `ProfileSnapshot` with the profile model/prompt/schema provenance used for that run. The API exposes the safe version metadata but not the raw profile snapshot, including in privacy exports. No second profile extraction pipeline is created for a mode: a profile cache is reused only while its three profile versions match the current operation; OCR fallback profiles are deliberately unversioned until the canonical text profile operation regenerates them.

The two result shapes are strict and provider-neutral. Job-targeted output contains `matchScore`, matched/missing skills and the five named breakdown dimensions; field-benchmark output contains `readinessScore` and its six named dimensions. Invalid mode/context combinations are rejected before enqueueing a job.

`career_goals` stores `target_role` (max 160), `seniority` (max 40), nullable `industry` (max 120), nullable `target_company` (max 160), nullable `target_job_description_id`, nullable `target_date` as a SQL `date`, required `active`, and nullable `deleted_at`. `DELETE` is a soft-delete transition that sets `active = false`, `deleted_at` and `updated_at`; normal Career Goal and active-goal queries exclude rows with `deleted_at IS NOT NULL`, and deleted rows cannot be reactivated. The foreign key to `job_descriptions` and the Learning Path foreign key are restrictive, so deleting a goal preserves its related path/history row. The service verifies that the referenced JD has the same owner. A PostgreSQL partial unique index on `user_id` where `active = true` backs the service-level row-lock transition and enforces one active goal per user.

### Skill Profile read model

`SkillProfile` is a computed, owner-scoped read model and is not a persisted entity/table. `GET /api/v1/skill-profile` projects and validates existing completed evidence from `resume_analyses`, `interview_reports`/`interview_answers`, `star_attempts` and `scenario_attempts`; B10 adds no `DbSet`, model snapshot change or migration. Numeric output is grouped by deterministic competency code and includes the equal-weight score, unique evidence count, latest evidence timestamp and source summaries. CV gaps/missing keywords remain qualitative weakness signals only. Raw CV, answer, STAR and scenario content is not returned.

### Learning Path persistence

B11 adds the normalized learning_paths, learning_path_milestones and learning_path_activities tables. A path is owned by UserId, references the same user's CareerGoalId, and has status, created_at and updated_at; a unique (UserId, CareerGoalId) index enforces one current path per user/goal while preserving paths for previous goals. Milestones have stable Code/SortOrder and activities have a stable per-path ActivityKey, type, deterministic metadata, optional CompetencyCode/ResourceId/ExternalUrl, priority/order, status and nullable CompletedAt.

Activity statuses are pending, completed and obsolete; only pending-to-completed is exposed by the API. Refresh reconciliation is additive: completed activities are never deleted or reset, pending activities no longer required become obsolete, and new gaps become pending rows. When a completed gap has newer Skill Profile evidence and remains below threshold, the original completed row is preserved and one deterministic evidence-cycle activity is added; repeated refresh with the same evidence reuses that pending row. The unique activity key and user-serialized transaction make initial generation and refresh idempotent under repeated/concurrent requests. Published Scenario IDs are selected server-side; a scenario gap without a published match becomes an external_learning activity with null resource/link, and B11 never accepts arbitrary resource IDs or URLs from the client.

Numeric gap policy is explicit: scores below 60 are critical priority, scores 60 through 74 are developing priority, and scores 75 or higher are not numeric gaps. Qualitative CV signals may produce supporting resume activities but never a numeric score. Progress is computed at read time as completed activities divided by non-obsolete activities; an empty path returns 0/0 and 0%. Learning Path rows are included in privacy export and deleted during account deletion before the Career Goal rows.

### Next Practice Recommendation read model

B12 adds no persisted model. `GET /api/v1/recommendations/next` flattens the authenticated user's existing Learning Path and reads the authenticated user's computed B10 Skill Profile. It returns one deterministic recommendation or `data: null` when no pending candidate remains; it never writes Learning Path rows, adds a `DbSet`, or requires a migration. The endpoint does not read raw practice tables or call an AI provider.

### Interview session state machine

```text
draft -> starting -> active -> completing -> completed
starting -> failed
active -> abandoned
```

Only `active` permits an official answer. Completion occurs exactly once and report generation is idempotent. `completed`, `failed`, `abandoned` are immutable terminal states except explicit audited administrative/recovery processes. State transition has optimistic concurrency/version to prevent duplicate answer, completion or report.

### Interview question contract v1

`interview_questions.sequence` is ordering metadata only. `kind` is the
server-owned semantic discriminator: `primary` questions have no parent,
while `followup` questions require `parent_question_id` pointing to an earlier
question in the same session. A follow-up keeps the parent's `topic`; malformed
kind, topic or parent relationships are rejected before mapping or evaluation.

The reserved free primary topics are ordered as `self_introduction`,
`behavioral_star`, then `motivation_role_fit`. A7 generates exactly these
primaries for the free portion; after Q3 the session remains `active` and its
continuation policy exposes finish-now versus upgrade-and-continue without
adding a `paywalled` session state. A paid continuation stays in the same
session and creates a server-selected paid `primary` topic; a `followup` is
created only when a paid behavioral evaluation has genuine missing STAR
evidence, with an explicit parent. The question-limit feature is a policy
snapshot and is separate from the interview session quota/reservation ledger.

### Usage event shape

```text
id, user_id, entitlement_id, action (reserve|consume|void|adjustment), quantity (+/-),
source_type, source_id, idempotency_key, created_at
```

Available quota is computed from entitlement limit plus ledger events, or maintained as a transactionally updated projection with the immutable ledger remaining the audit source of truth. Start reserves quota while creating the session in `starting`. After generating and validating the first usable question, one coherent PostgreSQL transaction persists that question, converts the reservation to `consume`, and transitions `starting → active`. Terminal failure before question persistence and activation converts the reservation to `void` and transitions `starting → failed` in one transaction. Once active, disconnect, refresh, navigation away, no answer or later AI/report failure does not automatically void usage; terminal report credit/support uses an audited `adjustment` per BR-08.

## 4. Data lifecycle

All values below are proposals only. DEC-03 owns the final production periods and approved user-facing policy; implementation must keep lifecycle policies configurable.

| Data | Default proposal | Notes |
| --- | --- | --- |
| Order/payment audit | Proposal: 7 years or local legal requirement | Confirm with provider/legal. |
| CV/JD/transcript | Until user deletes or account retention job executes | User-facing policy must state exact rule. |
| Audio/video | Off by default; 30 days only if opt-in | Requires separate consent. |
| Logs | Proposal: 30–90 days | Redact PII/content and secrets. |
| AI raw provider payload | Short operational retention | Store only metadata/output needed for audit. |
