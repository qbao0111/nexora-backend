using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Billing;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class BillingProviderTransactionCollisionTests : IDisposable
{
    private const string CollidingReference = "1000000000000000";
    private const string ReplacementReference = "1000000000000001";
    private readonly SequencedPayosProvider _provider = new(CollidingReference, ReplacementReference);
    private readonly NexoraApiFactory _factory;

    public BillingProviderTransactionCollisionTests()
    {
        _factory = CreateFactory(_provider);
        _factory.InitializeDatabase();
    }

    [Fact]
    public async Task ProviderTransactionCollisionRegeneratesReferenceBeforeCreatingCheckout()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var priceId = await SeedCollidingOrderAsync(_factory, account.UserId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions")
        {
            Content = JsonContent.Create(new { planPriceId = priceId })
        };
        request.Headers.Add("Idempotency-Key", "provider-reference-collision");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal([CollidingReference, ReplacementReference], _provider.GeneratedReferences);
        Assert.Equal(1, _provider.CreateCheckoutCalls);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var orderId = document.RootElement.GetProperty("data").GetProperty("orderId").GetGuid();
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var order = await verifyDb.Orders.SingleAsync(item => item.Id == orderId);
        Assert.Equal(ReplacementReference, order.ProviderTransactionId);
    }

    [Fact]
    public async Task ProviderTransactionCollisionStopsAfterBoundedAttempts()
    {
        var provider = new SequencedPayosProvider(Enumerable.Repeat(CollidingReference, 5).ToArray());
        using var factory = CreateFactory(provider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var priceId = await SeedCollidingOrderAsync(factory, account.UserId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions")
        {
            Content = JsonContent.Create(new { planPriceId = priceId })
        };
        request.Headers.Add("Idempotency-Key", "provider-reference-collision-bound");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(5, provider.GeneratedReferences.Count);
        Assert.Equal(0, provider.CreateCheckoutCalls);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PAYMENT_REFERENCE_COLLISION", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static NexoraApiFactory CreateFactory(SequencedPayosProvider provider) =>
        new(new Dictionary<string, string?>
        {
            ["Billing:Payment:Provider"] = "fake"
        }, services =>
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(provider);
        });

    private static async Task<Guid> SeedCollidingOrderAsync(NexoraApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var price = await db.PlanPrices.Include(item => item.Plan)
            .FirstAsync(item => item.AmountMinor > 0 && item.DurationDays != null);
        var now = DateTimeOffset.UtcNow;
        db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanPriceId = price.Id,
            PlanCodeSnapshot = price.Plan.Code,
            AmountMinor = price.AmountMinor,
            Currency = price.Currency,
            DurationDays = price.DurationDays,
            InterviewQuota = price.InterviewQuota,
            FeaturesSnapshot = "[]",
            Status = BillingValues.Failed,
            PaymentProvider = "payos",
            ProviderTransactionId = CollidingReference,
            CheckoutUrl = string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return price.Id;
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"collision-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123", displayName = "Collision candidate" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record Account(Guid UserId, string AccessToken);

    private sealed class SequencedPayosProvider(params string[] references) : IPaymentProvider
    {
        private readonly Queue<string> _references = new(references);

        public string ProviderName => "payos";
        public List<string> GeneratedReferences { get; } = [];
        public int CreateCheckoutCalls { get; private set; }

        public string CreateProviderTransactionId(Guid orderId)
        {
            _ = orderId;
            var reference = _references.Dequeue();
            GeneratedReferences.Add(reference);
            return reference;
        }

        public Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCheckoutCalls++;
            return Task.FromResult(new PaymentCheckout(
                ProviderName,
                request.ProviderTransactionId,
                new CheckoutAction("GET", $"https://pay.payos.vn/web/{request.ProviderTransactionId}", Array.Empty<CheckoutFormField>())));
        }

        public Task<VerifiedPaymentEvent> VerifyWebhookAsync(PaymentCallbackRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VerifiedPaymentEvent?> QueryPaymentAsync(PaymentOrderRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
