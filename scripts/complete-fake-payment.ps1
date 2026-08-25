[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [Guid] $OrderId,

    [string] $ApiUrl = 'http://localhost:5088'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$webhookSecret = 'nexora-local-development-only-webhook-secret'
$secretLines = & dotnet user-secrets list --project (Join-Path $repoRoot 'src/Nexora.Api')
if ($LASTEXITCODE -ne 0) { throw 'Unable to read local .NET user-secrets.' }
$secretLine = $secretLines | Where-Object { $_ -like 'Billing:FakePayment:WebhookSecret = *' } | Select-Object -First 1
if ($null -ne $secretLine) {
    $webhookSecret = $secretLine.Substring($secretLine.IndexOf(' = ') + 3)
}

$eventId = "frontend-dev-$([Guid]::NewGuid().ToString('N'))"
$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString([Globalization.CultureInfo]::InvariantCulture)
$body = @{
    eventId = $eventId
    orderId = $OrderId
    transactionId = "fake_$($OrderId.ToString('N'))"
    status = 'paid'
    occurredAt = [DateTimeOffset]::UtcNow
} | ConvertTo-Json -Compress

$hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($webhookSecret))
try {
    $signature = [Convert]::ToHexString(
        $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$timestamp.$body"))).ToLowerInvariant()
}
finally { $hmac.Dispose() }

$headers = @{
    'X-Payment-Timestamp' = $timestamp
    'X-Payment-Signature' = $signature
}
$result = Invoke-RestMethod -Method Post -Uri "$ApiUrl/api/v1/webhooks/payments/fake" `
    -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 15

Write-Host "Fake development payment completed for order $OrderId with status $($result.data.status)."
