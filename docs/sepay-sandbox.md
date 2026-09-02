# SePay Sandbox payment runbook

Nexora uses SePay Payment Gateway Sandbox only for internal testing. `FakePaymentProvider` remains the default automated-test adapter and DEC-02 production payment decisions are still deferred.

## Configure credentials

Run from the repository root. Keep values in user-secrets; do not put credentials in source control or frontend code.

```powershell
dotnet user-secrets set "Billing:Payment:Provider" "sepay" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:Environment" "Sandbox" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:MerchantId" "YOUR_SANDBOX_MERCHANT_ID" --project src/Nexora.Api
dotnet user-secrets set "Billing:Sepay:SecretKey" "YOUR_SANDBOX_SECRET_KEY" --project src/Nexora.Api
```

Restart both API and Worker after changing secrets. The checkout endpoint is `https://pay-sandbox.sepay.vn/v1/checkout/init`; reconciliation uses `https://pgapi-sandbox.sepay.vn` with Basic authentication. No production endpoint is accepted by the Development adapter.

## Configure the IPN URL

1. Start a public HTTPS tunnel: `ngrok http 5088`.
2. In the SePay Sandbox merchant dashboard set the IPN URL to `https://<ngrok-domain>/api/v1/webhooks/payments/sepay`.
3. Select `SECRET_KEY` authentication when the dashboard offers that option.

The IPN URL is a dashboard setting, not a checkout field. The browser must never receive `SecretKey`.

## Run and test

In another terminal:

```powershell
pwsh ./scripts/run-development.ps1
```

Open `http://localhost:5088/swagger` or connect the real frontend to `http://localhost:5088`.

1. Register/login and keep the access token in browser memory.
2. `GET /api/v1/plans` and choose a paid `planPriceId`.
3. `POST /api/v1/checkout-sessions` with a fresh `Idempotency-Key`.
4. The response contains `checkout.method=POST`, `checkout.url`, and an ordered `checkout.fields` array. Build a temporary HTML form, append hidden inputs in that exact order, and submit it to SePay. Do not use fetch or turn it into a GET query.
5. Complete the Sandbox checkout.
6. SePay sends `POST /api/v1/webhooks/payments/sepay` with `X-Secret-Key` and JSON. A valid or duplicate callback returns HTTP 200 `{ "success": true }`.
7. Poll `GET /api/v1/checkout-sessions/{orderId}` with Bearer auth. Expect `pending` while waiting, then `fulfilled` after the IPN; refetch `/api/v1/me` to see the entitlement.
8. If the IPN is delayed, call `POST /api/v1/checkout-sessions/{orderId}/refresh`. The server queries SePay and applies the same payment-event path. A `failed` order is terminal; start a new checkout intent.

## Safety notes

- Amount and currency come from the server-side plan snapshot (`VND` integer; no multiplier).
- Only authenticated IPN or server-side reconciliation can grant entitlement; browser return URLs are informational.
- The provider accepts `ORDER_PAID` (`CAPTURED` + `APPROVED`) and `TRANSACTION_VOID`. A final unpaid event moves a pending order to `failed` without creating an entitlement.
- Automated tests use mocked HTTP handlers and never call SePay.
