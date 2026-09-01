# VNPAY sandbox payment runbook

Nexora can use VNPAY Sandbox PAY 2.1.0 for internal Development/Staging-style hosted payment testing through the existing `IPaymentProvider` boundary. This does not resolve DEC-02 and does not enable production payments.

## Configuration

Keep secrets in user-secrets or environment variables, never in `appsettings*.json`.

```powershell
dotnet user-secrets set "Billing:Payment:Provider" "vnpay" --project src/Nexora.Api
dotnet user-secrets set "Billing:Vnpay:Environment" "Sandbox" --project src/Nexora.Api
dotnet user-secrets set "Billing:Vnpay:TmnCode" "YOUR_SANDBOX_TMN_CODE" --project src/Nexora.Api
dotnet user-secrets set "Billing:Vnpay:HashSecret" "YOUR_SANDBOX_HASH_SECRET" --project src/Nexora.Api
dotnet user-secrets set "Billing:Vnpay:ReturnUrl" "http://localhost:3000/payment/return" --project src/Nexora.Api
```

`Nexora.Api` and `Nexora.Worker` share the `Nexora.LocalDevelopment` user-secrets id. Restart both after changing secrets.

## Local flow

Terminal 1:

```powershell
ngrok http 5088
```

Configure the VNPAY sandbox merchant IPN URL outside Nexora:

```text
https://<ngrok-domain>/api/v1/webhooks/payments/vnpay
```

Terminal 2:

```powershell
pwsh ./scripts/run-development.ps1
```

API: `http://localhost:5088`
Swagger: `http://localhost:5088/swagger`
Frontend example: `http://localhost:3000`
Return URL: `http://localhost:3000/payment/return`

Then:

1. Login.
2. `GET /api/v1/plans`.
3. `POST /api/v1/checkout-sessions` with a server-owned paid `planPriceId` and an `Idempotency-Key`.
4. Open `data.checkoutUrl`.
5. Pay with a VNPAY sandbox test instrument.
6. VNPAY calls the configured public IPN URL with `GET /api/v1/webhooks/payments/vnpay?vnp_...`.
7. Nexora verifies checksum, `TmnCode`, transaction reference and amount.
8. Billing fulfills through the shared PaymentEvent path.
9. Frontend polls `GET /api/v1/checkout-sessions/{orderId}`.
10. Refetch `/api/v1/me` after status becomes `fulfilled`.

If IPN is delayed during manual testing, call:

```text
POST /api/v1/checkout-sessions/{orderId}/refresh
```

This uses VNPAY QueryDr to reconcile the pending order. The original order create date is used as `vnp_TransactionDate`.

Checkout status is terminal when it reaches `fulfilled` or `failed`; a failed order is not queried again and the user must start a new checkout intent. A correctly authenticated VNPAY IPN that reports a failed transaction still receives protocol `RspCode: "00"` (the IPN was processed), while the local order becomes `failed` and no entitlement is granted. Re-delivery of the same event returns `RspCode: "02"`.

## Safety notes

- Only `Billing:Vnpay:Environment=Sandbox` is supported in this code path.
- Nexora creates a signed PAY 2.1.0 URL locally; no provider create API call is made.
- VNPAY IPN URL is not a PAY parameter. Configure it in the VNPAY sandbox merchant portal.
- VNPAY ReturnUrl is browser navigation only and never grants entitlement.
- Amount/currency come from the server-owned plan price. The frontend must never submit price or quota.
- Invalid checksum, order reference, amount or currency does not grant entitlement.
- `FakePaymentProvider` remains available when `Billing:Payment:Provider=fake`.
- Production remains fail-closed until DEC-02 is approved.
