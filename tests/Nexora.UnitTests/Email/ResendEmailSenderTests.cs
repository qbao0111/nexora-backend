using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Email;
using Nexora.Integrations.Email;

namespace Nexora.UnitTests.Email;

public sealed class ResendEmailSenderTests
{
    [Fact]
    public async Task VerificationEmailUsesConfiguredSenderAndEscapesTemplateData()
    {
        Uri? requestUri = null;
        string? authorizationScheme = null;
        string? authorizationParameter = null;
        string? requestBody = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            requestUri = request.RequestUri;
            authorizationScheme = request.Headers.Authorization?.Scheme;
            authorizationParameter = request.Headers.Authorization?.Parameter;
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json(HttpStatusCode.OK, "{}");
        });
        using var httpClient = new HttpClient(handler);
        var sender = CreateSender(httpClient);

        await sender.SendVerificationAsync(
            new VerificationEmail(
                new EmailRecipient("candidate@example.test", "<b>Candidate</b>"),
                new Uri("https://frontend.example/verify?token=secret-token&user=123")),
            CancellationToken.None);

        Assert.Equal("https://api.resend.com/emails", requestUri!.AbsoluteUri);
        Assert.Equal("Bearer", authorizationScheme);
        Assert.Equal("test-only-email-key", authorizationParameter);

        using var body = JsonDocument.Parse(requestBody!);
        Assert.Equal("\"Nexora\" <no-reply@example.test>", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("\"<b>Candidate</b>\" <candidate@example.test>", body.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("Xác minh email Nexora", body.RootElement.GetProperty("subject").GetString());
        var html = body.RootElement.GetProperty("html").GetString()!;
        Assert.Contains("&lt;b&gt;Candidate&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("token=secret-token&amp;user=123", html, StringComparison.Ordinal);
        Assert.Contains("X&#225;c minh email", html, StringComparison.Ordinal);
        Assert.Contains("https://frontend.example/verify?token=secret-token&user=123", body.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordResetAndReminderUseVietnameseResponsiveTemplates()
    {
        var bodies = new List<JsonDocument>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            bodies.Add(await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken));
            return Json(HttpStatusCode.OK, "{}");
        });
        using var httpClient = new HttpClient(handler);
        var sender = CreateSender(httpClient);
        var recipient = new EmailRecipient("candidate@example.test", "Candidate");

        await sender.SendPasswordResetAsync(
            new PasswordResetEmail(recipient, new Uri("https://frontend.example/reset?token=reset-token")),
            CancellationToken.None);
        await sender.SendReminderAsync(
            new ReminderEmail(recipient, "Bạn đã hoàn thành <1/2> buổi luyện tập.", new Uri("https://frontend.example/practice")),
            CancellationToken.None);

        Assert.Equal(2, bodies.Count);
        Assert.Equal("Đặt lại mật khẩu Nexora", bodies[0].RootElement.GetProperty("subject").GetString());
        Assert.Contains("max-width:560px", bodies[0].RootElement.GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.Equal("Nhắc luyện tập Nexora", bodies[1].RootElement.GetProperty("subject").GetString());
        var reminderHtml = bodies[1].RootElement.GetProperty("html").GetString()!;
        Assert.Contains("&#227;", reminderHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;1/2&gt;", reminderHtml, StringComparison.Ordinal);
        Assert.Contains("Tiếp tục luyện tập", reminderHtml, StringComparison.Ordinal);

        foreach (var body in bodies) body.Dispose();
    }

    [Fact]
    public async Task ProviderFailuresAreNormalizedWithoutProviderBodyOrSecret()
    {
        const string providerSecret = "provider-secret-must-not-leak";
        var handler = new StubHandler((_, _) => Task.FromResult(
            Json(HttpStatusCode.Forbidden, $"{{\"message\":\"{providerSecret}\"}}")));
        using var httpClient = new HttpClient(handler);
        var sender = CreateSender(httpClient);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => sender.SendVerificationAsync(
            new VerificationEmail(
                new EmailRecipient("candidate@example.test"),
                new Uri("https://frontend.example/verify?token=private-token")),
            CancellationToken.None));

        Assert.Equal("EMAIL_PROVIDER_AUTH_FAILED", exception.Code);
        Assert.Equal(BusinessErrorKind.ExternalFailure, exception.Kind);
        Assert.DoesNotContain(providerSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRecipientOrLinkIsRejectedBeforeNetworkCall()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        });
        using var httpClient = new HttpClient(handler);
        var sender = CreateSender(httpClient);

        var invalidRecipient = await Assert.ThrowsAsync<BusinessException>(() => sender.SendVerificationAsync(
            new VerificationEmail(new EmailRecipient("not-an-email"), new Uri("https://frontend.example/verify")),
            CancellationToken.None));
        var invalidLink = await Assert.ThrowsAsync<BusinessException>(() => sender.SendPasswordResetAsync(
            new PasswordResetEmail(new EmailRecipient("candidate@example.test"), new Uri("file:///private/reset")),
            CancellationToken.None));

        Assert.Equal("EMAIL_INVALID_INPUT", invalidRecipient.Code);
        Assert.Equal("EMAIL_INVALID_INPUT", invalidLink.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NetworkFailureIsNormalizedAsUnavailable()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("contains no user data"));
        using var httpClient = new HttpClient(handler);
        var sender = CreateSender(httpClient);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => sender.SendReminderAsync(
            new ReminderEmail(new EmailRecipient("candidate@example.test"), "Tóm tắt luyện tập"),
            CancellationToken.None));

        Assert.Equal("EMAIL_PROVIDER_UNAVAILABLE", exception.Code);
        Assert.Equal(BusinessErrorKind.ExternalFailure, exception.Kind);
    }

    private static ResendEmailSender CreateSender(HttpClient httpClient) => new(
        httpClient,
        Options.Create(new EmailOptions
        {
            Provider = "resend",
            FromAddress = "no-reply@example.test",
            FromName = "Nexora"
        }),
        Options.Create(new ResendEmailOptions
        {
            ApiKey = "test-only-email-key",
            ApiBaseUrl = "https://api.resend.com",
            TimeoutSeconds = 15
        }));

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
