using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Speech;
using Nexora.Integrations.Speech;

namespace Nexora.UnitTests.Speech;

public sealed class AzureSpeechTokenProviderTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PostsEmptyBodyToValidatedRegionUsingSubscriptionKeyHeader()
    {
        const string key = "test-only-key-never-returned";
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://southeastasia.api.cognitive.microsoft.com/sts/v1.0/issueToken", request.RequestUri?.AbsoluteUri);
            Assert.Equal(key, Assert.Single(request.Headers.GetValues(AzureSpeechTokenProvider.SubscriptionKeyHeader)));
            Assert.Equal(0, request.Content?.Headers.ContentLength);
            Assert.Empty(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return Ok("short-token");
        });
        var provider = CreateProvider(handler, new ManualTimeProvider(StartTime), key);

        var token = await provider.GetAsync(CancellationToken.None);

        Assert.Equal("short-token", token.Token);
        Assert.Equal("southeastasia", token.Region);
        Assert.Equal(StartTime.AddMinutes(9), token.ExpiresAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ReusesCachedTokenAndRefreshesAtServerSafetyThreshold()
    {
        var responseCount = 0;
        var handler = new RecordingHandler((_, _) => Task.FromResult(Ok($"short-token-{Interlocked.Increment(ref responseCount)}")));
        var timeProvider = new ManualTimeProvider(StartTime);
        var provider = CreateProvider(handler, timeProvider);
        var first = await provider.GetAsync(CancellationToken.None);
        var reused = await provider.GetAsync(CancellationToken.None);

        Assert.Equal(first, reused);
        Assert.Equal("short-token-1", reused.Token);
        Assert.Equal(1, handler.CallCount);

        timeProvider.Advance(TimeSpan.FromMinutes(8));
        var refreshed = await provider.GetAsync(CancellationToken.None);

        Assert.Equal("short-token-2", refreshed.Token);
        Assert.Equal(timeProvider.GetUtcNow().AddMinutes(9), refreshed.ExpiresAt);
        Assert.Equal(2, handler.CallCount);

    }

    [Fact]
    public async Task ConcurrentCacheMissesIssueOnlyOneAzureRequest()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
            return Ok("shared-short-token");
        });
        var provider = CreateProvider(handler, new ManualTimeProvider(StartTime));

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => provider.GetAsync(CancellationToken.None)));

        Assert.Equal(1, handler.CallCount);
        Assert.All(results, result => Assert.Equal("shared-short-token", result.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccessResponseIsMappedWithoutLeakingKeyOrBody(HttpStatusCode statusCode)
    {
        const string key = "never-leak-test-key";
        const string body = "upstream response body must stay private";
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        }));
        var provider = CreateProvider(handler, new ManualTimeProvider(StartTime), key);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.GetAsync(CancellationToken.None));

        Assert.Equal("SPEECH_PROVIDER_UNAVAILABLE", exception.Code);
        Assert.Equal(BusinessErrorKind.ExternalFailure, exception.Kind);
        Assert.DoesNotContain(key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(body, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySuccessfulBodyIsMappedToSafeProviderFailure()
    {
        var provider = CreateProvider(new RecordingHandler((_, _) => Task.FromResult(Ok(" \r\n "))), new ManualTimeProvider(StartTime));

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.GetAsync(CancellationToken.None));

        Assert.Equal("SPEECH_PROVIDER_UNAVAILABLE", exception.Code);
        Assert.Equal(BusinessErrorKind.ExternalFailure, exception.Kind);
        Assert.DoesNotContain("Azure", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedSuccessfulBodyIsRejectedSafely()
    {
        var oversizedToken = new string('x', 16 * 1024 + 1);
        var provider = CreateProvider(
            new RecordingHandler((_, _) => Task.FromResult(Ok(oversizedToken))),
            new ManualTimeProvider(StartTime));

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.GetAsync(CancellationToken.None));

        Assert.Equal("SPEECH_PROVIDER_UNAVAILABLE", exception.Code);
        Assert.DoesNotContain(oversizedToken, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToProviderFailure()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Ok("unreachable");
        });
        var provider = CreateProvider(handler, new ManualTimeProvider(StartTime));
        using var cancellation = new CancellationTokenSource();
        var request = provider.GetAsync(cancellation.Token);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task HttpClientTimeoutBecomesSafeProviderFailure()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Ok("unreachable");
        });
        var provider = CreateProvider(handler, new ManualTimeProvider(StartTime), timeout: TimeSpan.FromMilliseconds(40));

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.GetAsync(CancellationToken.None));

        Assert.Equal("SPEECH_PROVIDER_UNAVAILABLE", exception.Code);
        Assert.Equal(BusinessErrorKind.ExternalFailure, exception.Kind);
    }

    [Fact]
    public async Task StartupValidationRejectsEnabledConfigurationWithoutKey()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartSpeechHostAsync(new Dictionary<string, string?>
        {
            ["Features:Speech"] = "true",
            ["Speech:Azure:Region"] = "southeastasia"
        }));
    }

    [Fact]
    public async Task StartupValidationRejectsEnabledConfigurationWithoutRegion()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartSpeechHostAsync(new Dictionary<string, string?>
        {
            ["Features:Speech"] = "true",
            ["Speech:Azure:Key"] = "test-only-key"
        }));
    }

    [Fact]
    public async Task DisabledSpeechCanStartWithoutKeyOrRegion()
    {
        await StartSpeechHostAsync(new Dictionary<string, string?>
        {
            ["Features:Speech"] = "false",
            ["Speech:Azure:Key"] = "",
            ["Speech:Azure:Region"] = ""
        });
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("eastus/path")]
    [InlineData("eastus:443")]
    [InlineData("eastus?x=y")]
    [InlineData("eastus#fragment")]
    [InlineData("east us")]
    [InlineData("éastus")]
    public async Task StartupValidationRejectsMalformedRegion(string region)
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartSpeechHostAsync(EnabledConfiguration(region: region)));
    }

    [Fact]
    public async Task StartupValidationRejectsRefreshAtOrAfterClientExpiry()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartSpeechHostAsync(EnabledConfiguration(
            serverRefreshMinutes: "9",
            clientUsableMinutes: "9")));
    }

    [Fact]
    public async Task StartupValidationRejectsClientExpiryAtAzureTokenLifetime()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => StartSpeechHostAsync(EnabledConfiguration(
            serverRefreshMinutes: "9",
            clientUsableMinutes: "10")));
    }

    private static AzureSpeechTokenProvider CreateProvider(
        RecordingHandler handler,
        ManualTimeProvider timeProvider,
        string key = "test-only-key",
        TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { Timeout = timeout ?? Timeout.InfiniteTimeSpan };
        return new AzureSpeechTokenProvider(
            new FixedHttpClientFactory(client),
            Options.Create(new AzureSpeechOptions
            {
                Key = key,
                Region = "southeastasia",
                TimeoutSeconds = 10,
                ServerRefreshMinutes = 8,
                ClientUsableMinutes = 9
            }),
            timeProvider,
            NullLogger<AzureSpeechTokenProvider>.Instance);
    }

    private static HttpResponseMessage Ok(string token) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(token, Encoding.UTF8, "text/plain")
    };

    private static async Task StartSpeechHostAsync(IReadOnlyDictionary<string, string?> values)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddSpeech(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
    }

    private static Dictionary<string, string?> EnabledConfiguration(
        string region = "southeastasia",
        string serverRefreshMinutes = "8",
        string clientUsableMinutes = "9") => new()
        {
            ["Features:Speech"] = "true",
            ["Speech:Azure:Key"] = "test-only-key",
            ["Speech:Azure:Region"] = region,
            ["Speech:Azure:ServerRefreshMinutes"] = serverRefreshMinutes,
            ["Speech:Azure:ClientUsableMinutes"] = clientUsableMinutes
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        private int _callCount;
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);
        public Task Started => _started.Task;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult(true);
            return send(request, cancellationToken);
        }
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
