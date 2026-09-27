# AI-generated content reporting and moderation

## Scope

`ContentReport` is separate from `ProductFeedback`: reports are private
owner-scoped compliance/moderation records and never enter public testimonials.
The API currently supports reports for released interview questions, ready
interview-answer evaluations, interview reports, completed resume analyses,
completed scenario evaluations and completed STAR evaluations. Skill Profile
has no persisted content ID; Learning Path is a deterministic read model, and
the scenario library is curated rather than a per-user generated result, so
those are not reportable through this endpoint.

## Submission

An authenticated user submits a canonical content type and UUID, a bounded
reason code, and an optional description of at most 1,000 characters to
`POST /api/v1/content-reports`. The route is limited to five requests per user
per 60-minute window by default. The server resolves the referenced row and
verifies the authenticated user owns or can view it before persisting anything;
missing, unavailable and foreign content fail closed. Reporter identity always
comes from the authenticated principal.

Clients cannot submit the moderation snapshot. After authorization, the server
copies only the generated output needed to review the report, bounded to 40,000
characters. Source CV/JD text, interview transcript/answers, source prompts and
provider payloads are not added separately. The candidate receives `202` with
only the report ID and received timestamp.

## Queue and state changes

Every moderation route requires the existing server-side `Admin` policy.

```text
pending -> reviewing -> resolved
                     -> dismissed
```

`GET /api/v1/admin/content-reports` supports status, content-type, reason and
date filters with bounded page pagination, newest first and deterministic ID
tie-break. Queue entries omit description and snapshot. An explicit detail read
returns the report description and the server-resolved snapshot; unrelated
identity fields and raw source inputs are not included.

`POST /api/v1/admin/content-reports/{id}/review` claims a pending report for the
calling admin. Only that moderator can resolve or dismiss it through
`POST /api/v1/admin/content-reports/{id}/resolve`, with a canonical outcome and
resolution code (`content_corrected`, `content_removed`, `no_action`, `other`)
plus an optional 1,000-character note. Repeating the same successful action is
safe; other invalid transitions return conflict. Optimistic concurrency allows
only one admin to claim a report at a time.

Transitions append safe metadata to the existing `AdminAuditEvent` stream:
report ID, action and resulting status only. Free-text descriptions, snapshots,
resolution notes, CVs, answers, AI outputs and tokens are not logged or attached
to telemetry. No escalation workflow, moderation SLA or staffed response
commitment is implemented or implied by this document.

## Retention and trends

Reports and snapshots are personal data owned by the reporter. The current
account-deletion worker explicitly deletes them; the personal-data export
includes the reporter's own report metadata and description but omits the
moderation snapshot and moderator notes. This is a technical lifecycle choice,
not a legal retention determination; any requirement to preserve anonymous
moderation evidence needs an explicit product/privacy-owner decision.

The admin queue can be filtered by canonical type, reason, status and date for
manual review. Aggregate trend dashboards, automatic filtering changes,
escalation routing and operational response targets are not implemented. Future
moderation or generation-filter changes should be based on a separately
approved review of report trends and should not treat unreviewed reports as
verified violations.
