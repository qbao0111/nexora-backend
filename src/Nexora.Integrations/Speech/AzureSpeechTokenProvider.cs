using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Speech;

namespace Nexora.Integrations.Speech;

public sealed partial class AzureSpeechTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AzureSpeechOptions> options,
    TimeProvider timeProvider,
    ILogger<AzureSpeechTokenProvider> logger) : ISpeechTokenProvider, IDisposable
{
    public const string HttpClientName = "AzureSpeechSts";
    public const string SubscriptionKeyHeader = "Ocp-Apim-Subscription-Key";
    private const int MaximumTokenResponseBytes = 16 * 1024;
    private static readonly TimeSpan AzureTokenLifetime = TimeSpan.FromMinutes(10);
    private readonly AzureSpeechOptions _options = options.Value;
    private readonly Uri _tokenEndpoint = new(
        $"https://{options.Value.Region}.api.cognitive.microsoft.com/sts/v1.0/issueToken",
        UriKind.Absolute);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CachedSpeechToken? _cachedToken;

    public async Task<SpeechAuthorizationToken> GetAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cached = Volatile.Read(ref _cachedToken);
        if (cached is not null && now < cached.RefreshAfter) return cached.Authorization;

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            cached = Volatile.Read(ref _cachedToken);
            if (cached is not null && now < cached.RefreshAfter) return cached.Authorization;

            var token = await RequestTokenAsync(cancellationToken);
            var issuedAt = timeProvider.GetUtcNow();
            var refreshAfter = issuedAt.AddMinutes(_options.ServerRefreshMinutes);
            var expiresAt = issuedAt.AddMinutes(_options.ClientUsableMinutes);
            if (expiresAt >= issuedAt + AzureTokenLifetime)
                throw ProviderUnavailable();

            var authorization = new SpeechAuthorizationToken(token, _options.Region, expiresAt);
            Volatile.Write(ref _cachedToken, new CachedSpeechToken(authorization, refreshAfter));
            return authorization;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose() => _refreshGate.Dispose();

    private async Task<string> RequestTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            };
            request.Headers.Add(SubscriptionKeyHeader, _options.Key);

            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogHttpFailure(logger, (int)response.StatusCode);
                throw ProviderUnavailable();
            }

            var token = await ReadTokenResponseAsync(response.Content, requestTimeout.Token);
            if (string.IsNullOrWhiteSpace(token))
            {
                LogInvalidResponse(logger);
                throw ProviderUnavailable();
            }

            return token.Trim();
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            LogRequestFailure(logger);
            throw ProviderUnavailable();
        }
    }

    private static async Task<string> ReadTokenResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumTokenResponseBytes)
            throw ProviderUnavailable();

        await using var responseStream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var tokenBytes = new MemoryStream();
            while (true)
            {
                var remaining = MaximumTokenResponseBytes + 1 - (int)tokenBytes.Length;
                var bytesRead = await responseStream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (bytesRead == 0) break;
                if (tokenBytes.Length + bytesRead > MaximumTokenResponseBytes)
                    throw ProviderUnavailable();

                tokenBytes.Write(buffer, 0, bytesRead);
            }

            return Encoding.UTF8.GetString(tokenBytes.GetBuffer(), 0, (int)tokenBytes.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static BusinessException ProviderUnavailable() => new(
        "SPEECH_PROVIDER_UNAVAILABLE",
        "Dịch vụ giọng nói tạm thời không khả dụng. Vui lòng thử lại.",
        BusinessErrorKind.ExternalFailure);

    [LoggerMessage(LogLevel.Warning, "Azure Speech token request failed with HTTP status {StatusCode}.")]
    private static partial void LogHttpFailure(ILogger logger, int statusCode);

    [LoggerMessage(LogLevel.Warning, "Azure Speech token request returned an empty token.")]
    private static partial void LogInvalidResponse(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Azure Speech token request failed due to a network or timeout error.")]
    private static partial void LogRequestFailure(ILogger logger);

    private sealed record CachedSpeechToken(
        SpeechAuthorizationToken Authorization,
        DateTimeOffset RefreshAfter);
}
