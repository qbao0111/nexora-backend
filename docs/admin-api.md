# Nexora Admin API Specification

**Base path:** `/api/v1/admin`  
**Authorization:** Required Bearer token for a user with the `Admin` role (`AuthorizationPolicies.Admin`).

All administrative operations require administrative privileges. Mutation endpoints also require an `Idempotency-Key` header where specified.

> **Privacy Guarantee:**
> Admin APIs NEVER expose raw candidate practice content, including extracted CV text, uploaded CV files, structured resume profiles, job description content, interview transcripts, scenario attempt answers, standalone STAR attempt answers, or evaluation feedback prose. Only administrative summaries, billing records, and audit events are accessible.

---

## 1. Plan and Price Management

### 1.1 List Plans
`GET /api/v1/admin/plans`
- Returns all plans (active and inactive) ordered by `sortOrder`, including prices and the feature matrix for each price.
- **Response 200:**
  ```json
  {
    "data": [
      {
        "id": "...",
        "code": "pro",
        "name": "Chuyên nghiệp",
        "description": "Gói luyện phỏng vấn nâng cao",
        "badge": "Phổ biến",
        "isHighlighted": true,
        "sortOrder": 3,
        "isActive": true,
        "createdAt": "2026-09-01T00:00:00Z",
        "prices": [
          {
            "id": "...",
            "amountMinor": 599000,
            "currency": "VND",
            "durationDays": 90,
            "interviewQuota": null,
            "isActive": true,
            "features": [
              { "featureDefinitionId": "...", "code": "scenario", "name": "Thực hành tình huống", "enabled": true, "limit": 20, "unlimited": false },
              { "featureDefinitionId": "...", "code": "star_builder", "name": "STAR Builder", "enabled": true, "limit": null, "unlimited": true }
            ]
          }
        ]
      }
    ]
  }
  ```

### 1.2 Create Plan
`POST /api/v1/admin/plans`
- **Request Body:**
  ```json
  {
    "code": "vip",
    "name": "VIP Coaching",
    "description": "Gói cao cấp",
    "badge": "VIP",
    "isHighlighted": false
  }
  ```
- **Response 201:** Returns created `AdminPlanView`.

### 1.3 Update Plan
`PATCH /api/v1/admin/plans/{id}`
- **Request Body:**
  ```json
  {
    "name": "VIP Coaching Updated",
    "description": "Mô tả mới",
    "badge": "Hot",
    "isHighlighted": true,
    "isActive": true
  }
  ```
- **Response 200:** Returns updated `AdminPlanView`.

### 1.4 Add Plan Price
`POST /api/v1/admin/plans/{id}/prices`
- **Request Body:**
  ```json
  {
    "amountMinor": 299000,
    "currency": "VND",
    "durationDays": 30,
    "interviewQuota": 10
  }
  ```
- **Response 201:** Returns created `AdminPlanPriceView`.

### 1.5 Update Plan Price
`PATCH /api/v1/admin/plan-prices/{priceId}`
- **Request Body:**
  ```json
  {
    "amountMinor": 299000,
    "currency": "VND",
    "durationDays": 30,
    "interviewQuota": 10,
    "isActive": false
  }
  ```
- **Response 200:** Returns updated `AdminPlanView`.
- **Note:** Prices with historical orders can be deactivated (`isActive = false`), but their commercial fields (`amountMinor`, `currency`, `durationDays`, `interviewQuota`) are immutable.

### 1.6 Update Price Feature Matrix
`PUT /api/v1/admin/plan-prices/{priceId}/features`
- Configures feature limits for this price tier.
- **Request Body:**
  ```json
  {
    "features": [
      { "featureCode": "cv_analysis", "enabled": true, "limit": 5 },
      { "featureCode": "scenario", "enabled": true, "limit": 10 },
      { "featureCode": "star_builder", "enabled": true, "limit": null },
      { "featureCode": "advanced_report", "enabled": true, "limit": null },
      { "featureCode": "progress_analytics", "enabled": true, "limit": null }
    ]
  }
  ```
- **Semantics:**
  - `enabled: false`: Feature disabled for this price tier.
  - `enabled: true, limit: N`: Feature limited to `N` uses.
  - `enabled: true, limit: null`: Feature unlimited.
  - Generic updates targeting `interview` are rejected (`INTERVIEW_FEATURE_IMMUTABLE`); interview quota is set on the plan price.
- **Snapshot invariant:** Modifying plan price features updates future checkouts and grants; existing active user entitlements retain their purchase snapshot.

### 1.7 List Feature Definitions
`GET /api/v1/admin/feature-definitions`
- Lists all platform feature definitions (`id`, `code`, `name`, `description`, `isActive`, `sortOrder`).
- **Response 200:** Returns list of feature definitions.

---

## 2. Scenario Library Management

### 2.1 Category Management
- `GET /api/v1/admin/scenario-categories` — List all scenario categories.
- `POST /api/v1/admin/scenario-categories` — Create category (`slug`, `name`, `description`).
- `PATCH /api/v1/admin/scenario-categories/{id}` — Update category (`name`, `description`, `isActive`).

### 2.2 Scenario Management
- `GET /api/v1/admin/scenarios` — List scenarios with filters (`query`, `categoryId`, `difficulty`, `competency`, `status`). Supports seeing `draft`, `published`, and `archived` states.
- `POST /api/v1/admin/scenarios` — Create scenario (`slug`, `title`, `summary`, `categoryId`, `difficulty`, `competency`, `estimatedMinutes`, `content`). Slug is required and validated. Initial status: `draft`.
- `GET /api/v1/admin/scenarios/{id}` — Get scenario detail including content.
- `PATCH /api/v1/admin/scenarios/{id}` — Update scenario mutable attributes (`title`, `summary`, `categoryId`, `difficulty`, `competency`, `estimatedMinutes`, `content`). Slug remains unchanged and does not need to be supplied.
- `POST /api/v1/admin/scenarios/{id}/publish` — Transition scenario to `published` (sets `publishedAt`).
- `POST /api/v1/admin/scenarios/{id}/archive` — Transition scenario to `archived`.

---

## 3. User Management and Support

### 3.1 List Users
`GET /api/v1/admin/users?query=...&role=...&planCode=...&entitlementState=...&cursor=...&pageSize=20`
- Query filters by email or display name substring.
- Filter by `role` (e.g. `Admin`, `Candidate`).
- Filter by `planCode` (e.g. `free`, `pro`).
- Filter by `entitlementState` (`active`, `expired`, `none`).
- Cursor-based pagination on `id`.

### 3.2 Get User Detail
`GET /api/v1/admin/users/{userId}`
- Returns user account summary, current active entitlement with feature quotas, and recent orders.
- **Strict Privacy Invariant:** No CV text, JD body, answers, transcripts, or reports are returned.
- **Response 200:**
  ```json
  {
    "data": {
      "id": "...",
      "email": "candidate@example.com",
      "displayName": "Nguyen Van A",
      "roles": ["Candidate"],
      "active": true,
      "createdAt": "2026-09-01T00:00:00Z",
      "currentEntitlement": {
        "id": "...",
        "planCode": "pro",
        "startsAt": "2026-09-01T00:00:00Z",
        "endsAt": "2026-11-30T00:00:00Z",
        "features": [
          { "code": "interview", "name": "Phỏng vấn", "enabled": true, "limit": null, "reserved": 0, "consumed": 5, "adjustment": 0, "available": null, "unlimited": true },
          { "code": "scenario", "name": "Thực hành tình huống", "enabled": true, "limit": 20, "reserved": 0, "consumed": 3, "adjustment": 0, "available": 17, "unlimited": false }
        ]
      },
      "recentOrders": [...]
    }
  }
  ```

### 3.3 Manual Plan Grant
`POST /api/v1/admin/users/{userId}/plan-grants`
- Headers: `Idempotency-Key: <unique-uuid>`
- **Request Body:**
  ```json
  {
    "planPriceId": "...",
    "replaceCurrent": true,
    "reason": "Customer support compensation"
  }
  ```
- Creates an entitlement snapshot based on the current configuration of the specified `planPriceId`.
- Logs an immutable `AdminAuditEvent`.
- **Response 201:** Returns `AdminGrantResponse`.

### 3.4 Quota Adjustment
`POST /api/v1/admin/users/{userId}/feature-adjustments`
- Headers: `Idempotency-Key: <unique-uuid>`
- **Request Body:**
  ```json
  {
    "featureCode": "scenario",
    "quantity": 5,
    "reason": "Promotional bonus"
  }
  ```
- Adjusts available quota for the specified feature. Supports negative adjustments for revocations (rejects adjustments that would cause available balance to go below zero).
- Works for both canonical `interview` quota and generic features (`cv_analysis`, `scenario`, `star_builder`).
- Logs an immutable `AdminAuditEvent` and transactional usage event.
- **Response 200:** Returns `AdminAdjustmentResponse`.
