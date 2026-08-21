# Data Model Specification — Nexora

**Status:** Approved implementation baseline; retention values deferred  
**Last updated:** 2026-08-21

## 1. Aggregates và ownership

```text
ApplicationUser 1--N Resume 1--N ResumeAnalysis
ApplicationUser 1--N InterviewSession 1--N InterviewQuestion 1--1 InterviewAnswer
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
| `job_descriptions`, `resume_analyses` | JD và output analysis | input snapshot/model/prompt version. |
| `interview_sessions`, `interview_questions`, `interview_answers`, `interview_reports` | Practice loop | answer unique per official question, session state machine. |
| `star_drafts`, `scenario_attempts` | Practice support | owner ID, version/status. |
| `idempotency_keys`, `outbox_events`, `audit_logs` | Reliability/operations | expiry/retention job. |

## 3. Required columns

All user-owned records: `id UUID/ULID`, `user_id`, `created_at timestamptz`, `updated_at timestamptz`; add `(user_id, created_at DESC)` index for user history. Soft-delete only where recovery/retention requires it; otherwise hard-delete file content after legal retention period.

### Interview session state machine

```text
draft -> starting -> active -> completing -> completed
starting -> failed
active -> abandoned
```

Only `active` permits an official answer. Completion occurs exactly once and report generation is idempotent. `completed`, `failed`, `abandoned` are immutable terminal states except explicit audited administrative/recovery processes. State transition has optimistic concurrency/version to prevent duplicate answer, completion or report.

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
