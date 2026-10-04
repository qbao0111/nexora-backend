# AI content reporting — coordinated rollout, not yet deployed

Existing POST /api/v1/content-reports now accepts learning_path and skill_profile in addition to the six existing canonical types. Existing auth, rate limits, receipt, admin review/resolve and account-deletion path remain in use. No raw CV/JD/answer/client snapshot is accepted as a report snapshot.

GET /api/v1/skill-profile adds nullable reportingId. Nonempty derived profiles receive a server-generated SHA256-based owner/content/version reference; it is NOT a persisted SkillProfile entity or an authorization token. On submission the server recomputes the caller's current profile and requires the same ID. Empty/stale/foreign IDs fail opaque 404. Learning Path uses its actual persisted path ID, owner and nondeleted CareerGoal, usable active/completed state and milestones.

Snapshots are immutable at acceptance, valid JSON, limited to 200 complete entries / 40,000 characters with truncated flag. Admin detail is the protected view; queues do not disclose snapshot content.

## Required migration

20261004165820_GrowthContentReportTypes expands the existing PostgreSQL CK_content_reports_content_type check constraint. This is required by the actual model/schema: without it, otherwise-valid inserts fail. Old migrations are unchanged. No Site Content migration or fake SkillProfile entity exists.

Down restores the old constraint and will fail if new-type reports remain. Do not delete reports to force rollback; assess/reconcile retained moderation records before any rollback.

## Order and compatibility

1. Review/approve paired BE and Mobile PRs independently. No merge/deploy in this task.
2. Owner deployment: apply BE migration through existing release process, deploy compatible code, confirm API + Worker health. Do not run concurrent per-instance migrations.
3. Publish/distribute Mobile only after compatible BE verified. An older BE rejects the new content types; older skill-profile responses lack reportingId so new Mobile disables reporting and requests reload rather than inventing an ID.
4. Device smoke: report real safe Learning Path and Skill Profile output, observe 202 data.reportId/receivedAt, then verify protected admin queue/detail/review/resolve. Refresh changed profile before retrying 404. Do not automatically retry an uncertain POST; no deduplication/Idempotency-Key contract was added.
5. Confirm rejected/malformed IDs and 401/404/409/429/5xx/network errors never show success; only a valid 202 receipt does. No quota, generation or payment behavior changes.

Public Privacy/Terms drafts and owner-only publication checklist are in site-content/. Prepared text is NOT published policy; reconcile actual fresh admin/public bodies and tokens before authorized publication. No effective date chosen or production authenticated write performed.
