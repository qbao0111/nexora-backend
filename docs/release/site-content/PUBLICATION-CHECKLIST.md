# Privacy / Terms — owner approval and admin publication required

These are proposed complete Markdown bodies, NOT published legal documents or legal approval. No authenticated production request was made. No new effective date was selected.

## Evidence and preservation

Read-only public API snapshots retrieved on 2026-10-04 from the Render fallback origin:

- https://nexora-backend-q32b.onrender.com/api/v1/public/pages/privacy
- https://nexora-backend-q32b.onrender.com/api/v1/public/pages/terms

The custom API also returned the same published Privacy/Terms bodies after a slow response. Both API origins were read-only verified; re-read the current public revision and website before publication.

Privacy: title "Chính sách bảo mật"; effectiveAt 2026-09-25T00:00:00+00:00; publishedAt 2026-10-04T13:52:52.835691+00:00.
Terms: title "Điều khoản dịch vụ"; effectiveAt null; publishedAt 2026-10-04T13:53:04.278294+00:00.
Public concurrencyToken is null: NEVER use it as the admin update token.

The snapshot files preserve the actual public Markdown. The proposed files contain complete replacement bodies. The .diff files show exact changes against those snapshots, not against an imagined template. Privacy sections 1, 2, 6, 7 and 9 are unchanged; sections 3, 4, 8 replaced and 5 extended. Terms sections 1, 2, 4, 5, 7, 9 and 10 are unchanged; 3, 6 and 8 receive additive paragraphs. Web pricing, payment confirmation and refund wording remain.

## Comparison with Mobile declarations

- Generic AI wording does not identify DeepSeek or distinguish extracted document text from original file upload. Draft does; local PDF/DOCX extraction uses no Gemini OCR.
- Generic infrastructure wording omits Render, Neon, R2, Resend and optional Azure Speech. Draft names them and distinguishes mobile Sentry removal from backend telemetry.
- Public deletion wording says "ẩn danh" without disclosing retained linkable UUIDs. Draft describes pseudonymous financial/usage/audit identifiers, incomplete requests, storage/upload delays, legal holds and backup/provider limitations.
- 30-day logs / 90-day non-accounting usage are targets, not implemented guarantees. Twelve-month eligibility is conditional, not a deletion SLA; retention defaults are disabled/report-only.
- Optional Speech audio processing, temporary device recordings and stored transcripts are distinct. No universal Microsoft zero-retention statement is made.
- Mobile-new-release feedback opt-in is qualified; do not infer every historical client behaves identically.
- Terms preserve web payments and add consumption-only Android wording.
- Official email and external deletion URL are included.

Sources: Mobile release/PUBLISHED-PRIVACY-APPROVAL-CHECKLIST.md and PLAY-CONSOLE-DATA-SAFETY-FINAL.md; Backend docs/privacy-retention.md and docs/privacy-retention-policy.json. Actual account agreements, released configuration and legal identity still need owner confirmation. DeepSeek sharing declaration is not evidence that model training occurs.

## Publisher/controller identity — unresolved owner gate

Current public Privacy/Terms identify the product as Nexora and give its contact email, but do not state an accountable legal person/entity. Play publisher identity was NOT inspected. Therefore identity accuracy cannot be confirmed. Owner must compare the actual Play listing with the responsible controller/publisher, approve any required identity text and supply only verified details. No legal company name or address is invented in these drafts.

## Existing supported API

SiteContentController and SiteContentService already support Admin-authorized draft editing, draft readback/preview, independent public published content and explicit publishing. GET admin returns draft even if isPublished=true (that flag means some published version exists). PUT does NOT publish; publish copies draft into published fields. Each mutation rotates concurrencyToken and writes an audit event. There is no built-in separate legal approval record or rendered preview endpoint: approval is a human release gate and Markdown rendering is a review step.

No endpoint, migration, FE change or Site Content code fix is needed for these documents. Bodies are below 30,000 characters and contain no HTML angle brackets.

## Exact publication procedure — authorized owner/admin only

Do not execute until owner approval. Use normal authenticated admin access; never include access tokens in documents, logs or PRs. API base is https://api.nexorainterview.io.vn/api/v1. Header: Authorization: Bearer ADMIN_ACCESS_TOKEN. JSON writes use Content-Type: application/json. No Site Content Idempotency-Key contract exists.

For each key privacy, then terms:

1. Obtain explicit owner/legal approval of complete text, named providers, actual release/configuration, controller identity and proposed sharing decisions. Record approval privately. Do not publish claims of deployed reporting support before deployment verification.
2. GET /public/pages/KEY and GET /admin/site-pages/KEY. Both normally return 200 with data. Save current published and draft rollback copies securely; compare fresh public text with the snapshot and the admin draft with public text. Reconcile ANY newer content or unpublished admin work. Do not overwrite from these snapshots.
3. Preserve current title, about=null and effectiveAt from the reconciled admin draft unless an owner explicitly approves a different date. Public Privacy's old effectiveAt is not automatic approval of a new date; Terms may remain null.
4. PUT /admin/site-pages/KEY, using the latest admin GET token and COMPLETE reconciled body:

```json
{
  "title": "CURRENT_APPROVED_TITLE",
  "bodyMarkdown": "COMPLETE_APPROVED_MARKDOWN",
  "about": null,
  "effectiveAt": null,
  "concurrencyToken": "LATEST_ADMIN_GET_UUID"
}
```

The null effectiveAt shown is an example for Terms, not instruction to clear Privacy's date. Supply the preserved/explicitly approved value. JSON-escape the full Markdown using a JSON serializer, not manual string replacement.

5. Successful PUT returns 200 data with a NEW token. GET /admin/site-pages/KEY again; confirm text/title/date and token match the successful PUT. Render that draft for review. Public GET must still expose the previous published version. Owner approves this exact complete draft; if another edit occurs, approval must be refreshed.
6. After approval, POST /admin/site-pages/KEY/publish with the latest unchanged draft token:

```json
{ "concurrencyToken": "APPROVED_DRAFT_UUID" }
```

Expect 200 data and a newly rotated token/publishedAt. Never reuse the pre-PUT token. No atomic two-page publish exists; approve both first and verify each separately.
7. 409 SITE_CONTENT_CONFLICT: STOP, re-read both representations, reconcile other admin changes and obtain renewed approval; never blind-retry overwriting. 400 SITE_CONTENT_INVALID: correct invalid complete body/HTML/length/date/request before renewed review. 401/403: stop and use legitimate authorized access. 404: confirm key and existing draft; do not invent endpoints. 5xx/network uncertainty: GET admin/public to determine actual outcome before another mutation.
8. Rollback is a NEW owner-approved draft update/publish using fresh tokens and reconciled saved content, never an old-token replay or direct DB edit.

## Post-publication public verification

- Allow the API's public max-age=300 cache to expire; fetch public Privacy and Terms from both the custom API and fallback. Compare complete bodies/title/effectiveAt and new publishedAt against the approved draft, not just HTTP 200.
- Open https://www.nexorainterview.io.vn/privacy and /terms on desktop and Mobile; confirm rendered content is current, readable and links work. Verify https://www.nexorainterview.io.vn/account-deletion is reachable; do not trigger a destructive deletion test.
- Confirm DeepSeek, local extraction/no Gemini OCR, all named infrastructure, optional Speech, pseudonymous UUID retention, exceptions/holds/provider backups, official contact and web/Android distinction.
- Confirm no promised automatic 30/90-day erasure, universal anonymity, guaranteed hire, invented legal identity or automatic report acceptance.
- Reconcile Play Data Safety categories against actual released transfers and owner-approved applicable agreements/configuration. Record actual publication evidence and remaining release gates; a prepared draft is not POLICY READY.
- Do not request admin credentials, rotate secrets or call production PUT/publish as part of this preparation.

**SITE CONTENT PRIVACY DRAFT READY — OWNER APPROVAL AND ADMIN PUBLICATION REQUIRED.**
