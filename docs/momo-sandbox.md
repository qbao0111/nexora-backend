# MoMo sandbox payment runbook

Nexora can use MoMo sandbox for internal Development/Staging-style testing through the existing `IPaymentProvider` boundary. This does not resolve DEC-02 and does not enable production payments.

## Configuration

Keep secrets in user-secrets or environment variables, never in `appsettings*.json`.

```powershell
dotnet user-secrets set "Billing:Payment:Provider" "momo" --project src/Nexora.Api
dotnet user-secrets set "Billing:MoMo:Environment" "Sandbox" --project src/Nexora.Api
dotnet user-secrets set "Billing:MoMo:PartnerCode" "YOUR_SANDBOX_PARTNER_CODE" --project src/Nexora.Api
dotnet user-secrets set "Billing:MoMo:AccessKey" "YOUR_SANDBOX_ACCESS_KEY" --project src/Nexora.Api
dotnet user-secrets set "Billing:MoMo:SecretKey" "YOUR_SANDBOX_SECRET_KEY" --project src/Nexora.Api
dotnet user-secrets set "Billing:MoMo:RedirectUrl" "http://localhost:3000/payment/return" --project src/Nexora.Api
dotnet user-secrets set "Billing:MoMo:IpnUrl" "PUBLIC_TUNNEL_URL/api/v1/webhooks/payments/momo" --project src/Nexora.Api
```

`Nexora.Api` and `Nexora.Worker` share the `Nexora.LocalDevelopment` user-secrets id. Restart both after changing secrets.

## Local flow

1. Run API and Worker in Development.
2. Login from the frontend.
3. `GET /api/v1/plans`.
4. `POST /api/v1/checkout-sessions` with a server-owned paid `planPriceId` and an `Idempotency-Key`.
5. Redirect the browser to `data.checkoutUrl`.
6. MoMo calls `POST /api/v1/webhooks/payments/momo` through the configured public tunnel URL.
7. Frontend polls `GET /api/v1/checkout-sessions/{orderId}` and refetches `/me` when status becomes `fulfilled`.
8. If IPN is delayed during manual testing, call `POST /api/v1/checkout-sessions/{orderId}/refresh` to query MoMo sandbox and reconcile the same order.

## Safety notes

- Only `Billing:MoMo:Environment=Sandbox` is supported in this code path.
- Production still fails closed for payment before DEC-02.
- Amount/currency come from the server-owned plan price. The frontend must never submit price or quota.
- The webhook returns `204 No Content` for processed/duplicate valid deliveries.
- Invalid signature, order reference, amount or currency does not grant entitlement.
- `FakePaymentProvider` remains available when `Billing:Payment:Provider=fake`.
