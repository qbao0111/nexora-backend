# payOS local and staging setup

`PayosPaymentProvider` is an explicit adapter behind `IPaymentProvider`. It uses the [official payOS .NET SDK](https://payos.vn/docs/sdks/back-end/net/) and is not the local default: keep `fake` for deterministic automated tests and only select `payos` when deliberately exercising a real payment flow.

payOS currently uses its production Merchant API rather than a separate sandbox. Treat local and staging tests as real transactions, use a small amount, and never point an automated test at payOS. See the [official test-environment note](https://payos.vn/docs/moi-truong-test/).

## Configure secrets

Keep all three credentials in .NET user-secrets for a local API process, or in the deployment secret store for staging. Do not put them in `.env.example`, source control, logs or frontend code.

```powershell
dotnet user-secrets set "Billing:Payment:Provider" "payos" --project src/Nexora.Api
dotnet user-secrets set "Billing:Payos:ClientId" "YOUR_PAYOS_CLIENT_ID" --project src/Nexora.Api
dotnet user-secrets set "Billing:Payos:ApiKey" "YOUR_PAYOS_API_KEY" --project src/Nexora.Api
dotnet user-secrets set "Billing:Payos:ChecksumKey" "YOUR_PAYOS_CHECKSUM_KEY" --project src/Nexora.Api
dotnet user-secrets set "Billing:Payos:ReturnUrl" "http://localhost:3000/payment/success" --project src/Nexora.Api
dotnet user-secrets set "Billing:Payos:CancelUrl" "http://localhost:3000/payment/cancel" --project src/Nexora.Api
dotnet user-secrets set "Billing:Payos:TimeoutSeconds" "15" --project src/Nexora.Api
```

For staging, use public HTTPS return and cancel URLs, for example `https://staging-frontend.example/payment/success` and `https://staging-frontend.example/payment/cancel`. Startup requires non-empty credentials, absolute callback URLs and a timeout from 5 to 60 seconds. Local HTTP is accepted only for loopback browser URLs.

## Configure the webhook

In the payOS dashboard/payment channel, configure this public HTTPS callback URL:

```text
https://<PUBLIC-API-HOST>/api/v1/webhooks/payments/payos
```

payOS sends a signed JSON webhook to that URL. Nexora verifies it with `Billing:Payos:ChecksumKey`, resolves its persisted numeric `orderCode` through the unique `(payment_provider, provider_transaction_id)` order index, validates VND and the server-owned amount, then runs the normal idempotent fulfillment transaction. The payOS dashboard's signed verification probe is acknowledged without creating an order or entitlement. This follows payOS's [webhook signature guidance](https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/).

The browser URLs are different from the backend webhook URL. `ReturnUrl` and `CancelUrl` must go to frontend pages; they may show a result and call `POST /api/v1/checkout-sessions/{orderId}/refresh`, but they never mark payment as successful themselves.

## Run the flow

1. Create a checkout session with a fresh `Idempotency-Key`.
2. Redirect the browser to `checkout.url` because the response has `checkout.method: "GET"` and no form fields.
3. Wait for the signed webhook, or use the authenticated refresh endpoint if the webhook is delayed.
4. Poll the checkout status and refetch `/api/v1/me` after `fulfilled`.

The payOS adapter uses the official .NET SDK and never exposes the Client ID, API Key or Checksum Key to the browser. Production payment remains disabled by the existing DEC-02 gate until the required refund, invoice and tax decisions are approved.
