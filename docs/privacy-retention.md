# Privacy retention foundation — operator review required

Source: `PrivacyService`, `ExternalAccountDeletionService`, `RetentionProcessor`, `NexoraDbContext`, `BillingEntities`, `FeatureEntities`, `UploadIntentStore`, `R2StorageProvider`. Requirements: FR-AUTH-04 / NFR-PRIV-01. Owner-approved targets are not legal approval or evidence of deployed/provider enforcement. No policy is published by this PR. Machine-readable status: [manifest](privacy-retention-policy.json).

## Source-grounded retention inventory

| Entities / category | Current disposition and purpose | Residual identifiers / limits |
| --- | --- | --- |
| `ApplicationUser`, Identity claims/logins/roles/tokens, `UserProfile`, refresh tokens | Acceptance revokes sessions; completion clears credentials, profile and Identity children, replaces email/username, locks login. Retain account row for financial FK integrity. | Stable UUID, synthetic `deleted-<id>@invalid.local`, creation/deletion/security stamps remain linkable, not anonymous. No account-row purge. |
| Resumes, StoredFile, CV text/profile, analyses, JD, career goals, learning paths/milestones/activities, interview sessions/questions/answers/reports, Scenario/STAR attempts | Explicit owner purge in account deletion; career content is deleted on completion. Worker/user locking prevents in-flight AI results from recreating private rows. | External AI copies are not erased by deleting DB rows. Individual resume soft-delete retains historical snapshots; account deletion is separate. |
| Avatars and owner upload objects/intents | Explicit object deletion before DB purge, using keys from owner rows. An unexpired signed PUT defers completion until expiry and another object deletion. | Keys, filenames, checksums, token hash and owner UUID until successful purge. Transient storage failure retries up to three attempts then visible `failed`; no completion SLA. |
| Refresh sessions, realtime notifications, owner idempotency records, reporter ContentReports, ProductFeedback | Removed by account deletion. | Idempotency key/fingerprint/resource ID, notification resource ID, report free text/snapshots and feedback are linkable/private before removal. |
| `ExternalDeletionVerification` | Existing per-owner cleanup plus new bounded global expiry cleanup capability; no incoming entity FK references it. | Hash (not raw token), owner UUID, timestamps. Only `ExpiresAt <= cutoff`; never unexpired tokens. Holds exempt rows. |
| `DataPrivacyRequest` | Privacy auditing. New cleanup capability only for completed account-deletion requests aged at least configured 12 calendar months AND owner `DeletedAt` at least that old. | User/request UUIDs, client idempotency key, safe error and status/attempt metadata. No incoming EF FK; not financial evidence. Incomplete/failed jobs and their owner's completed audit rows are preserved. Active accounts and held owners excluded. |
| `Order`, `PaymentEvent` | Retained for accounting/payment evidence, refund/dispute correlation and owner history. NEVER purged here. | User UUID, provider transaction/event IDs, checkout URLs/action snapshots and feature/price snapshots remain linkable; URLs may contain provider session capabilities. Minimization needs a separate verified provider/history review. |
| `Subscription`, `Entitlement`, `EntitlementFeature` | Retained for billing/quota/FK integrity. NEVER purged here. | User/order/subscription IDs, usage counters, dates. A free trial is not proof that every dependent row is non-accounting. |
| `UsageEvent`, `FeatureUsageEvent` | Immutable reserve/consume/void/adjustment accounting/quota ledgers. **90-day non-accounting purge BLOCKED**. | User/entitlement IDs, source/resource UUIDs, idempotency keys and free-text `Reason` can link deleted accounts to transactions/practice. Incoming `InterviewSession.ReservationEventId` restricts deletion; Scenario/STAR reservation references and billing replay logic require cross-row analysis. |
| `AdminAuditEvent` | Retain security/moderation/financial audit; **automatic 12-month purge BLOCKED** pending category separation and legal/security sign-off. | Admin FK, target type/string ID, free-text reason and JSON metadata; action alone does not prove a row is safe to delete. May describe billing adjustments/disputes or another active account. Not treated as completed privacy-job metadata. |
| Remaining `OutboxEvent` | Personal aggregate jobs removed on account deletion; financial/outstanding jobs retained. | Generic JSON payload and aggregate UUIDs can be linkable. No generic age purge; processed does not prove non-financial. |
| Remaining `IdempotencyRecord` | Owner records removed on account deletion; active-account/provider replay guarantees retained. | Actor/resource IDs, key/fingerprint. No global age purge that would weaken replay/financial guarantees. |
| Site assets/settings/pages, scenario catalogue, plans/prices/features | Global product/admin content, not personal career purge. | `SiteAsset.UploadedBy` restrictive FK, asset key and audit author remain. Do not delete active/public assets by age. |
| Retention checkpoint | One row, overwritten with latest metadata-only counters/status/time; restart schedule and failure backoff. | No owner ID or content. Not a growing execution history. |
| Retention hold | Owner-UUID or global exemption for legitimate legal/dispute/fraud/accounting/security reasons; separate operator management. | Reason is constrained enum, no name/email/raw case narrative. Active holds have no automatic expiry. Released hold metadata is **not automatically purged**; owner/legal must approve its audit lifecycle and remove only released records under the same lock when no longer needed. |
| Application/security logs, Sentry, backups, email/AI/speech provider copies | 30-day Nexora-controlled log target; provider lifecycle verification required. | Console/platform sink is not a backend DB log table. No code here enforces platform log, backup or external-copy deletion. |

### Why the 90-day rule is blocked; concrete migration plan

Do not delete `UsageEvent` / `FeatureUsageEvent` just because `User.DeletedAt` is 90 days old. The current schema lacks an authoritative legal retention category or reliable non-accounting boundary. Rows form immutable financial/quota chains, with restrictive and logical references, unique replay identities and free-text linkability.

Follow-up, not implemented in this PR:

1. Business/legal owners classify events, replay tombstones, adjustments, orders and audit actions; define holds and legally necessary fields/durations by category, without a blanket five/ten-year claim.
2. Add an explicitly reviewed retention category, purpose and completion anchor to a **separate non-accounting telemetry table** (or migrate only positively classified data out of financial ledgers). Add a nullable opaque account-deletion anchor and a compact replay record only where needed.
3. Preserve reservation/finalization chains and FK/replay evidence. Do not rewire `InterviewSession.ReservationEventId` or edit immutable ledgers to enable deletion; minimize personal free text with a separately approved migration after reference/financial reconciliation.
4. Backfill classification fail-closed: unknown stays retained pending review. Test refunds, order history, quota totals, replay and deleted/active owners on PostgreSQL before enabling 90-day purge.

## Implementation and safe defaults

Dedicated `RetentionWorker` checks every five minutes, independently of practice polls. The persisted singleton checkpoint gates actual sweeps (default six hours). Disabled mode does not open a DB connection. PostgreSQL is required; non-PostgreSQL returns `unsupported_database` without deleting.

Every sweep uses `pg_try_advisory_xact_lock(761604031)` inside a transaction. Other replicas return `locked`; restart reads `NextRunAt`. Lock releases on commit/rollback/connection loss, so there is no stale lease timeout. **All hold writes and manual checkpoint resets MUST use the same transaction advisory lock.** Restrict DB writer access to approved operators; this is not a user/admin public API.

Each category selects at most `BatchSize` eligible IDs with stable `(expiry/completion, UUID)` order; held/unsafe samples are separately bounded and cannot starve later eligible rows. Counters describe sampled rows, not a complete backlog census: `examined = eligible + skipped`, `removed` is committed deletes, `failed` counts failed batches (0/1). Unexpired/recent rows are outside the sample, not counted as skipped.

Queries never fetch token hashes, idempotency keys, raw content or financial data into logs. The two delete statements and checkpoint commit together; a savepoint rolls back all partial deletes on error while retaining the replica lock. Failure schedules exponential backoff (30 minutes, 60, 120..., capped one day); five consecutive failures suspend cleanup pending an operator fix/reset. Cancellation rolls back the transaction. DB-unavailable polling errors retry only on the five-minute timer; no short-poll busy loop.

| Worker environment key | Default / supported range |
| --- | --- |
| `Privacy__Retention__Enabled` | `false` — entire processor disabled |
| `Privacy__Retention__DryRun` | `true` — report only |
| `Privacy__Retention__PurgeEnabled` | `false` — independent deletion gate; false forces report-only even if DryRun=false |
| `Privacy__Retention__SweepIntervalHours` | `6`, 1–24 |
| `Privacy__Retention__BatchSize` | `100`, 1–1000 **per category** |
| `Privacy__Retention__VerificationGraceHours` | `0`, 0–24 after expiry |
| `Privacy__Retention__AuditRetentionMonths` | `12`, 12–120 calendar months after completed deletion |
| `Privacy__Retention__FailureBackoffMinutes` | `30`, 15–1440 |
| `Privacy__Retention__MaxConsecutiveFailures` | `5`, 1–10 |

Approximately 24-hour verification cleanup is a **target**, not an unconditional SLA: disabled purge, holds, failed/suspended workers and a backlog may exceed it. Twelve months is a configured eligibility threshold, not a promise of a physical deletion date. No 30-day grace/cancellation period exists.

## Manual enablement / holds / rollback runbook

1. Review this PR, category/legal exemptions and manifest. Apply **new** `PrivacyRetentionLifecycle` migration once through release tooling (two new tables, two existing cleanup indexes; no data purge). Never rewrite old migrations. PostgreSQL tests apply the full migration chain; production DB is untouched by this task.
2. Verify backup/restore and provider checklist below. Keep `Enabled=false`, `DryRun=true`, `PurgeEnabled=false` until approved. Deploy only with explicit human instruction; this task does not deploy.
3. Enable `Enabled=true` with report-only gates still set. Inspect `retention_checkpoints` plus structured counters, worker health and eligible samples privately via SQL. Validate no financial/active-account row is in scope. Dry-run advances the durable schedule; it does not remove eligible rows.
4. Create necessary holds **before** purge enablement. Approved operator transaction (replace placeholders, never use email/name as identifier):

```sql
BEGIN;
SELECT pg_advisory_xact_lock(761604031);
INSERT INTO retention_holds ("Id", "UserId", "ReasonCode", "CreatedAt", "ReleasedAt")
VALUES ('<new-hold-uuid>', '<opaque-user-uuid>', 'legal', now(), NULL);
-- UserId=NULL means a global hold, including active-account expired verification rows.
COMMIT;
```

Release only after the responsible legal/security owner authorizes it:

```sql
BEGIN;
SELECT pg_advisory_xact_lock(761604031);
UPDATE retention_holds SET "ReleasedAt"=now() WHERE "Id"='<approved-hold-uuid>' AND "ReleasedAt" IS NULL;
COMMIT;
```

Holds apply to **this retention processor**, not cancellation of a user's canonical account-deletion request; career data deletion/session revocation remains unchanged. Financial/security tables outside this processor are preserved regardless of holds. Do not use holds to claim provider erasure or silently override user deletion.

5. Only after reviewed dry-run evidence and explicit approval set `DryRun=false` AND `PurgeEnabled=true`. A running batch uses the configuration it started with; disabling config takes effect after restart/next scope, not retroactive recovery. Rollback configuration stops future purges but cannot restore already-deleted data.
6. After a failure, investigate safe status/counters and DB connectivity/permissions/constraints. Under the same advisory lock, reset `ConsecutiveFailures=0, NextRunAt=NULL` **only after fixing the cause**. Do not routinely reset the timer to defeat backoff. Checkpoint access is operator-only; no frontend dependency.

## R2 / orphan review

Canonical account deletion reads only that owner's `StoredFile`, `UploadIntent` and avatar keys, then calls `IStorageProvider.DeleteAsync`. R2 validates each key and issues a scoped `DeleteObject`; success is required before DB deletion, and an unexpired PUT capability causes a later retry. Existing fake R2/ownership tests are not evidence of live provider lifecycle/versions.

This PR adds **no storage delete**, bucket listing or lifecycle rule. Never add age-expiry to the shared CV/avatar bucket: active-user documents must survive. Orphan intents/objects remain a distinct **BLOCKED** cleanup follow-up: persist claims/retry state, lock the owner/finalize path, wait for signed PUT expiry, confirm no `StoredFile`, avatar, site asset or completed intent references the exact key, delete idempotently and keep failed metadata. Do not infer orphanhood from object age or delete unexpired intents. No production bucket settings/data were changed.

## Infrastructure verification checklist (all unverified by this PR)

Record actual plan/settings, region, source URL/evidence, check date and accountable owner; never paste secrets. Change manifest to `PROVIDER-CONFIRMED` only after real evidence, and `LEGAL-APPROVED` only with owner/legal approval.

- **Neon:** backup/PITR branches, recovery window, snapshot/export copies, deletion/restore lifecycle and post-restore reapplication of completed deletion evidence. No invented 30-day backup promise.
- **Render / Nexora-controlled log sinks:** actual searchable/archive log windows, exports, access and deletion ability; configure/verify 30-day standard target where supported. Backend console logging alone does not enforce it.
- **Cloudflare R2:** private bucket, exact-key deletion, versions/copies and any retention/hold settings; audit abandoned uploads separately; no bucket-wide age expiry.
- **Resend:** delivery/message contents, suppression/audit retention, account/DPA settings and deletion support.
- **Azure Speech:** actual SDK/token flow, region, audio/text processing, logging and retention for the contracted account.
- **DeepSeek:** contracted API-input/output retention/training policy, region, account/DPA and erasure support. A text-only adapter is not a zero-retention guarantee.
- **Historical Gemini:** previous document/text copies, account retention and erasure support; OCR removal prevents new document calls, not erasure of historical copies.
- **Sentry:** actual project event retention, exports, access, scrubbing and deletion; DSN absence/presence or sanitizer does not prove lifecycle.
- **Accounting / disputes / security:** responsible owner sets final legally necessary categories, identifiers and retention/exemptions, not a universal duration for all billing tables.

## Vietnamese disclosure draft — NOT PUBLISHED / requires legal review

Khi bạn yêu cầu xóa tài khoản, Nexora thu hồi phiên đăng nhập và xử lý việc xóa dữ liệu nghề nghiệp, tệp CV, câu trả lời và báo cáo trong hệ thống. Tài khoản được vô hiệu hóa cho đăng nhập và thông tin nhận diện được thay thế sau khi tác vụ hoàn tất. Nếu việc xóa tệp gặp lỗi hoặc đường dẫn tải lên còn hiệu lực, trạng thái xử lý có thể kéo dài; yêu cầu chưa hoàn tất không được coi là đã xóa.

Một số mã giao dịch, dữ liệu thanh toán, lịch sử sử dụng liên quan đến kế toán và thông tin kiểm toán tối thiểu có thể được giữ lại để đáp ứng nghĩa vụ pháp lý, giải quyết tranh chấp hoặc bảo vệ an toàn hệ thống. Các mã lưu lại vẫn có thể liên kết với tài khoản đã xóa; đây không phải cam kết mọi bản ghi đều được ẩn danh hoàn toàn.

Mục tiêu lưu giữ thông thường là 30 ngày đối với nhật ký do Nexora kiểm soát, 90 ngày đối với dữ liệu sử dụng không thuộc kế toán sau khi hoàn tất xóa tài khoản và 12 tháng đối với thông tin kiểm toán yêu cầu xóa, có ngoại lệ hợp lệ. Đây hiện là mục tiêu chính sách, không phải cam kết thời hạn xóa tự động cho mọi loại dữ liệu. Cơ chế dọn dữ liệu định kỳ chỉ được bật sau khi kiểm tra và phê duyệt; việc tách dữ liệu sử dụng khỏi dữ liệu tài chính vẫn đang được hoàn thiện.

Các bản sao lưu và bản sao tại nhà cung cấp bên ngoài tuân theo vòng đời thực tế đã được xác minh của từng dịch vụ. Việc xóa dữ liệu trong Nexora không tự động chứng minh mọi bản sao bên ngoài đã bị xóa. Thời hạn và ngoại lệ áp dụng phải được công bố sau khi được người phụ trách pháp lý xác nhận.
