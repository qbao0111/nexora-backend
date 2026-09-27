using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Privacy;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class PrivacyDeletionPostgresTests
{
    [PostgresFact]
    public async Task ConcurrentExternalConfirmationsConsumeTokenAndQueueDeletionOnlyOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        await using var factory = NexoraApiFactory.CreatePostgres(connectionString);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var email = $"external-deletion-{Guid.NewGuid():N}@example.test";
        using (var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new
               {
                   email,
                   password = "Strong!Pass123",
                   displayName = "Deletion candidate"
               }))
            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);

        using (var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" }))
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using (var request = await client.PostAsJsonAsync("/api/v1/account-deletion/external/request", new { email }))
            Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        var token = TestEmailInbox.GetAccountDeletionToken(email);
        var attempts = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v1/account-deletion/external/confirm", new { token }),
            client.PostAsJsonAsync("/api/v1/account-deletion/external/confirm", new { token }));
        using var first = attempts[0];
        using var second = attempts[1];

        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.BadRequest));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var verification = await db.ExternalDeletionVerifications.SingleAsync();
        Assert.NotNull(verification.ConsumedAt);
        Assert.Equal(1, await db.DataPrivacyRequests.CountAsync());
        var refreshTokens = await db.RefreshTokens.ToArrayAsync();
        Assert.NotEmpty(refreshTokens);
        Assert.All(refreshTokens, refresh => Assert.NotNull(refresh.RevokedAt));
        Assert.Equal(PrivacyValues.Queued, (await db.DataPrivacyRequests.SingleAsync()).Status);
    }
}
