using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Business.Ai;
using Nexora.Integrations.Ai;

namespace Nexora.IntegrationTests.Ai;

public sealed class DeepSeekAiProviderTests
{
    private const string CorrelationId = "provider-diagnostic-correlation";
    private const string PrivateProviderContent = "not-json-provider-private-raw-token";

    [Theory]
    [InlineData("{}", "choices_missing", 0, "none")]
    [InlineData("{\"choices\":[]}", "choices_empty", 0, "none")]
    [InlineData("{\"choices\":[{}]}", "message_missing", 0, "none")]
    [InlineData("{\"choices\":[{\"message\":{}}]}", "content_missing", 0, "none")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null},\"finish_reason\":\"stop\"}]}", "content_invalid", 0, "stop")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"   \"},\"finish_reason\":\"stop\"}]}", "content_blank", 3, "stop")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"not-json-provider-private-raw-token\"},\"finish_reason\":\"stop\"}]}", "content_json_parse_failed", 35, "stop")]
    public async Task InvalidEnvelopeShapesEmitSafeStageDiagnostics(
        string responseBody,
        string expectedStage,
        int expectedContentLength,
        string expectedFinishReason)
    {
        var logger = new RecordingLogger<DeepSeekAiProvider>();
        var provider = CreateProvider(responseBody, logger);
        using var schema = JsonDocument.Parse("{}");

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.GenerateStructuredAsync<GeneratedQuestion>(CreateRequest(schema), CancellationToken.None));

        Assert.Equal(AiProviderFailureKind.InvalidResponse, exception.Kind);
        Assert.Equal("AI provider returned an invalid structured response.", exception.Message);
        if (expectedStage == "content_json_parse_failed")
        {
            Assert.Null(exception.InnerException);
            Assert.Equal(AiProviderRetryHint.MalformedStructuredOutput, exception.RetryHint);
        }
        else
        {
            Assert.Null(exception.InnerException);
        }

        var diagnostic = Assert.Single(logger.Messages, message =>
            message.Contains($"structuredFailureStage={expectedStage}", StringComparison.Ordinal));
        Assert.Contains("purpose=interview.evaluate", diagnostic, StringComparison.Ordinal);
        Assert.Contains($"finishReason={expectedFinishReason}", diagnostic, StringComparison.Ordinal);
        Assert.Contains($"contentLength={expectedContentLength}", diagnostic, StringComparison.Ordinal);
        Assert.Contains("lineNumber=", diagnostic, StringComparison.Ordinal);
        Assert.Contains("bytePositionInLine=", diagnostic, StringComparison.Ordinal);
        Assert.Contains($"correlationId={CorrelationId}", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateProviderContent, string.Join(Environment.NewLine, logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateProviderContent, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedProviderEnvelopeEmitsSafeParseDiagnostic()
    {
        var logger = new RecordingLogger<DeepSeekAiProvider>();
        var provider = CreateProvider(PrivateProviderContent, logger);
        using var schema = JsonDocument.Parse("{}");

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            provider.GenerateStructuredAsync<GeneratedQuestion>(CreateRequest(schema), CancellationToken.None));

        Assert.Equal(AiProviderFailureKind.InvalidResponse, exception.Kind);
        Assert.Equal("AI provider returned an invalid structured response.", exception.Message);
        var diagnostic = Assert.Single(logger.Messages, message =>
            message.Contains("structuredFailureStage=envelope_json_parse_failed", StringComparison.Ordinal));
        Assert.Contains("purpose=interview.evaluate", diagnostic, StringComparison.Ordinal);
        Assert.Contains("finishReason=none", diagnostic, StringComparison.Ordinal);
        Assert.Contains("contentLength=0", diagnostic, StringComparison.Ordinal);
        Assert.Contains($"correlationId={CorrelationId}", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateProviderContent, string.Join(Environment.NewLine, logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateProviderContent, exception.ToString(), StringComparison.Ordinal);
    }

    private static DeepSeekAiProvider CreateProvider(string responseBody, RecordingLogger<DeepSeekAiProvider> logger)
    {
        var handler = new StaticResponseHandler(responseBody);
        var options = new DeepSeekOptions { ApiKey = "test-key", TimeoutSeconds = 5 };
        return new DeepSeekAiProvider(new HttpClient(handler), Options.Create(options), logger);
    }

    private static AiRequest CreateRequest(JsonDocument schema) => new(
        AiPurposes.InterviewEvaluate,
        "prompt-v1",
        "model-v1",
        "rubric-v1",
        "schema-v1",
        "candidate input",
        schema,
        512,
        CorrelationId);

    private sealed class StaticResponseHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
