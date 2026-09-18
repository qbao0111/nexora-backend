using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Speech;

namespace Nexora.IntegrationTests;

public sealed class SpeechApiTests
{
    [Fact]
    public async Task AnonymousRequestIsUnauthorized()
    {
        var speech = new RecordingSpeechTokenProvider();
        using var factory = CreateFactory(speech, new Dictionary<string, string?>
        {
            ["Features:Speech"] = "true",
            ["Speech:Azure:Key"] = "test-only-speech-key",
            ["Speech:Azure:Region"] = "southeastasia"
        });
        using var client = factory.CreateHttpsClient();

        using var response = await client.PostAsync($"/api/v1/speech/interviews/{Guid.NewGuid()}/token", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, speech.CallCount);
    }

    [Fact]
    public async Task OwnerGetsShortLivedTokenButForeignAndUnknownInterviewsRemainOpaqueNotFound()
    {
        const string subscriptionKey = "test-only-permanent-speech-key";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(9);
        var speech = new RecordingSpeechTokenProvider(new("test-short-lived-token", "southeastasia", expiresAt));
        using var factory = CreateFactory(speech, new Dictionary<string, string?>
        {
            ["Features:Speech"] = "true",
            ["Speech:Azure:Key"] = subscriptionKey,
            ["Speech:Azure:Region"] = "southeastasia"
        });
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        var interviewId = await StartInterviewAsync(ownerClient, "speech-owner-start");

        using var ownerResponse = await ownerClient.PostAsync($"/api/v1/speech/interviews/{interviewId}/token", null);
        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        Assert.True(ownerResponse.Headers.CacheControl?.NoStore);
        Assert.True(ownerResponse.Headers.CacheControl?.Private);
        Assert.Contains(ownerResponse.Headers.Pragma, value => string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
        Assert.True(ownerResponse.Content.Headers.TryGetValues("Expires", out var expiresValues));
        Assert.Equal("0", Assert.Single(expiresValues));
        var ownerBody = await ownerResponse.Content.ReadAsStringAsync();
        using var ownerJson = JsonDocument.Parse(ownerBody);
        var tokenData = ownerJson.RootElement.GetProperty("data");
        Assert.Equal("test-short-lived-token", tokenData.GetProperty("token").GetString());
        Assert.Equal("southeastasia", tokenData.GetProperty("region").GetString());
        Assert.Equal(expiresAt, tokenData.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.DoesNotContain(subscriptionKey, ownerBody, StringComparison.Ordinal);
        Assert.Equal(1, speech.CallCount);

        using var foreignClient = factory.CreateHttpsClient();
        var foreignUser = await RegisterAsync(foreignClient);
        foreignClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreignUser.AccessToken);
        using var foreignResponse = await foreignClient.PostAsync($"/api/v1/speech/interviews/{interviewId}/token", null);
        using var missingResponse = await foreignClient.PostAsync($"/api/v1/speech/interviews/{Guid.NewGuid()}/token", null);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal("NOT_FOUND", await ErrorCodeAsync(foreignResponse));
        Assert.Equal("NOT_FOUND", await ErrorCodeAsync(missingResponse));
        Assert.Equal(1, speech.CallCount);
    }

    [Fact]
    public async Task DisabledFeatureReturnsCanonical503WithoutCallingProvider()
    {
        var speech = new RecordingSpeechTokenProvider();
        using var factory = CreateFactory(speech, new Dictionary<string, string?>
        {
            ["Features:Speech"] = "false",
            ["Speech:Azure:Key"] = "",
            ["Speech:Azure:Region"] = ""
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var response = await client.PostAsync($"/api/v1/speech/interviews/{Guid.NewGuid()}/token", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("FEATURE_DISABLED", await ErrorCodeAsync(response));
        Assert.Equal(0, speech.CallCount);
    }

    [Fact]
    public async Task SpeechTokenHasIndependentUserRateLimitAndCanonicalRetryAfter()
    {
        var speech = new RecordingSpeechTokenProvider();
        using var factory = CreateFactory(speech, new Dictionary<string, string?>
        {
            ["Features:Speech"] = "true",
            ["Speech:Azure:Key"] = "test-only-speech-key",
            ["Speech:Azure:Region"] = "southeastasia",
            ["RateLimits:SpeechToken:PermitLimit"] = "2",
            ["RateLimits:SpeechToken:WindowMinutes"] = "15"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "speech-rate-limit-start");

        using var first = await client.PostAsync($"/api/v1/speech/interviews/{interviewId}/token", null);
        using var second = await client.PostAsync($"/api/v1/speech/interviews/{interviewId}/token", null);
        using var limited = await client.PostAsync($"/api/v1/speech/interviews/{interviewId}/token", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null || limited.Headers.TryGetValues("Retry-After", out _));
        Assert.Equal("RATE_LIMITED", await ErrorCodeAsync(limited));
        Assert.Equal(2, speech.CallCount);
    }

    private static NexoraApiFactory CreateFactory(
        RecordingSpeechTokenProvider speech,
        IReadOnlyDictionary<string, string?> overrides) =>
        new(overrides, services =>
        {
            services.RemoveAll<ISpeechTokenProvider>();
            services.AddSingleton<ISpeechTokenProvider>(speech);
        });

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"speech-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Speech test candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var payload = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var user = payload.RootElement.GetProperty("data").GetProperty("user");
        var token = payload.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        return new Account(user.GetProperty("id").GetGuid(), token!);
    }

    private static async Task<Guid> StartInterviewAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new { role = "Backend Developer", seniority = "junior", interviewType = "technical", difficulty = "medium" })
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private sealed record Account(Guid Id, string AccessToken);

    private sealed class RecordingSpeechTokenProvider : ISpeechTokenProvider
    {
        private readonly SpeechAuthorizationToken _token;
        private int _callCount;

        public RecordingSpeechTokenProvider()
            : this(new SpeechAuthorizationToken("test-short-lived-token", "southeastasia", DateTimeOffset.UtcNow.AddMinutes(9)))
        {
        }

        public RecordingSpeechTokenProvider(SpeechAuthorizationToken token) => _token = token;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<SpeechAuthorizationToken> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(_token);
        }
    }
}
