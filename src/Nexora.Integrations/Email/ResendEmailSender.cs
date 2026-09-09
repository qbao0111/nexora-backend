using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Email;

namespace Nexora.Integrations.Email;

public sealed class ResendEmailSender(
    HttpClient httpClient,
    IOptions<EmailOptions> emailOptions,
    IOptions<ResendEmailOptions> resendOptions) : IEmailSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EmailOptions _emailOptions = emailOptions.Value;
    private readonly ResendEmailOptions _resendOptions = resendOptions.Value;

    public Task SendVerificationAsync(VerificationEmail message, CancellationToken cancellationToken) =>
        SendAsync(EmailTemplateRenderer.Verification(message), cancellationToken);

    public Task SendPasswordResetAsync(PasswordResetEmail message, CancellationToken cancellationToken) =>
        SendAsync(EmailTemplateRenderer.PasswordReset(message), cancellationToken);

    public Task SendReminderAsync(ReminderEmail message, CancellationToken cancellationToken) =>
        SendAsync(EmailTemplateRenderer.Reminder(message), cancellationToken);

    private async Task SendAsync(RenderedEmail email, CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_resendOptions.ApiBaseUrl.TrimEnd('/')}/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _resendOptions.ApiKey.Trim());
        request.Content = JsonContent.Create(new ResendEmailRequest(
            FormatAddress(_emailOptions.FromAddress, _emailOptions.FromName),
            [FormatAddress(email.Recipient.Address, email.Recipient.DisplayName)],
            email.Subject,
            email.HtmlBody,
            email.TextBody), options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ProviderUnavailable();
        }
        catch (HttpRequestException)
        {
            throw ProviderUnavailable();
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            throw MapFailure(response.StatusCode);
        }
    }

    private void ValidateConfiguration()
    {
        if (!string.Equals(_emailOptions.Provider, EmailConfigurationRules.ResendProvider, StringComparison.OrdinalIgnoreCase) ||
            !EmailConfigurationRules.IsValidFromAddress(_emailOptions.FromAddress) ||
            !EmailConfigurationRules.IsValidDisplayName(_emailOptions.FromName) ||
            !EmailConfigurationRules.IsValidApiKey(_resendOptions.ApiKey) ||
            !EmailConfigurationRules.IsOfficialResendBaseUrl(_resendOptions.ApiBaseUrl) ||
            _resendOptions.TimeoutSeconds is < 1 or > 60)
            throw InvalidConfiguration();
    }

    private static string FormatAddress(string address, string? displayName)
    {
        var parsed = new MailAddress(address.Trim(), displayName?.Trim() ?? string.Empty);
        return parsed.ToString();
    }

    private static BusinessException MapFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new BusinessException(
            "EMAIL_PROVIDER_AUTH_FAILED",
            "Không thể xác thực với dịch vụ email.",
            BusinessErrorKind.ExternalFailure),
        HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => ProviderUnavailable(),
        _ => new BusinessException(
            "EMAIL_PROVIDER_INVALID_RESPONSE",
            "Dịch vụ email trả về phản hồi không hợp lệ.",
            BusinessErrorKind.ExternalFailure)
    };

    private static BusinessException InvalidConfiguration() => new(
        "EMAIL_PROVIDER_INVALID_CONFIG",
        "Cấu hình dịch vụ email không hợp lệ.",
        BusinessErrorKind.ExternalFailure);

    private static BusinessException ProviderUnavailable() => new(
        "EMAIL_PROVIDER_UNAVAILABLE",
        "Dịch vụ email hiện không khả dụng.",
        BusinessErrorKind.ExternalFailure);

    private sealed record ResendEmailRequest(
        string From,
        IReadOnlyCollection<string> To,
        string Subject,
        string Html,
        string Text);
}

internal sealed record RenderedEmail(
    EmailRecipient Recipient,
    string Subject,
    string HtmlBody,
    string TextBody);

internal static class EmailTemplateRenderer
{
    public static RenderedEmail Verification(VerificationEmail message)
    {
        ValidateCommon(message.Recipient, message.VerificationLink);
        var recipientName = DisplayName(message.Recipient);
        var link = Link(message.VerificationLink);
        return new RenderedEmail(
            message.Recipient,
            "Xác minh email Nexora",
            Layout(
                recipientName,
                "Xác minh email của bạn",
                "Chào mừng bạn đến với Nexora. Hãy xác minh email để bảo vệ tài khoản và sử dụng đầy đủ tính năng luyện phỏng vấn.",
                "Xác minh email",
                link),
            $"Chào {recipientName},\n\nHãy xác minh email Nexora của bạn tại:\n{message.VerificationLink.AbsoluteUri}\n\nNếu bạn không tạo tài khoản, hãy bỏ qua email này.");
    }

    public static RenderedEmail PasswordReset(PasswordResetEmail message)
    {
        ValidateCommon(message.Recipient, message.ResetLink);
        var recipientName = DisplayName(message.Recipient);
        var link = Link(message.ResetLink);
        return new RenderedEmail(
            message.Recipient,
            "Đặt lại mật khẩu Nexora",
            Layout(
                recipientName,
                "Đặt lại mật khẩu",
                "Chúng tôi nhận được yêu cầu đặt lại mật khẩu Nexora của bạn. Liên kết này chỉ dùng được trong thời gian giới hạn.",
                "Đặt lại mật khẩu",
                link),
            $"Chào {recipientName},\n\nBạn có thể đặt lại mật khẩu Nexora tại:\n{message.ResetLink.AbsoluteUri}\n\nNếu bạn không yêu cầu thao tác này, hãy bỏ qua email này.");
    }

    public static RenderedEmail Reminder(ReminderEmail message)
    {
        ValidateRecipient(message.Recipient);
        if (string.IsNullOrWhiteSpace(message.Summary) || message.Summary.Trim().Length > 2_000)
            throw InvalidEmailInput();
        if (message.PracticeLink is not null && !EmailConfigurationRules.IsSafeLink(message.PracticeLink))
            throw InvalidEmailInput();

        var recipientName = DisplayName(message.Recipient);
        var summary = message.Summary.Trim();
        var encodedSummary = WebUtility.HtmlEncode(summary);
        var action = message.PracticeLink is null
            ? string.Empty
            : $"<p style=\"margin:24px 0 0\"><a href=\"{Link(message.PracticeLink)}\" style=\"display:inline-block;background:#2563eb;color:#fff;padding:12px 20px;border-radius:8px;text-decoration:none;font-weight:600\">Tiếp tục luyện tập</a></p>";
        var textAction = message.PracticeLink is null ? string.Empty : $"\n\nTiếp tục luyện tập tại:\n{message.PracticeLink.AbsoluteUri}";
        return new RenderedEmail(
            message.Recipient,
            "Nhắc luyện tập Nexora",
            Layout(
                recipientName,
                "Nhắc bạn luyện tập",
                encodedSummary,
                null,
                null,
                action),
            $"Chào {recipientName},\n\n{summary}{textAction}");
    }

    private static string Layout(
        string recipientName,
        string heading,
        string body,
        string? buttonText,
        string? buttonLink,
        string? extraHtml = null)
    {
        var button = buttonText is null || buttonLink is null
            ? string.Empty
            : $"<p style=\"margin:24px 0\"><a href=\"{buttonLink}\" style=\"display:inline-block;background:#2563eb;color:#fff;padding:12px 20px;border-radius:8px;text-decoration:none;font-weight:600\">{WebUtility.HtmlEncode(buttonText)}</a></p>";
        return $"""
            <!doctype html>
            <html lang="vi">
              <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{WebUtility.HtmlEncode(heading)}</title>
              </head>
              <body style="margin:0;background:#f3f4f6;color:#172033;font-family:Arial,sans-serif;line-height:1.6">
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="width:100%;background:#f3f4f6">
                  <tr><td align="center" style="padding:32px 16px">
                    <table role="presentation" cellspacing="0" cellpadding="0" style="width:100%;max-width:560px;background:#fff;border-radius:12px">
                      <tr><td style="padding:32px">
                        <p style="margin:0 0 24px;color:#2563eb;font-size:22px;font-weight:700">Nexora</p>
                        <h1 style="margin:0 0 16px;font-size:24px;line-height:1.3">{WebUtility.HtmlEncode(heading)}</h1>
                        <p style="margin:0">Chào {WebUtility.HtmlEncode(recipientName)},</p>
                        <p style="margin:16px 0 0">{body}</p>
                        {button}
                        {extraHtml}
                        <p style="margin:28px 0 0;color:#667085;font-size:13px">Đây là email tự động từ Nexora. Vui lòng không trả lời email này.</p>
                      </td></tr>
                    </table>
                  </td></tr>
                </table>
              </body>
            </html>
            """;
    }

    private static void ValidateCommon(EmailRecipient recipient, Uri link)
    {
        ValidateRecipient(recipient);
        if (!EmailConfigurationRules.IsSafeLink(link)) throw InvalidEmailInput();
    }

    private static void ValidateRecipient(EmailRecipient recipient)
    {
        if (recipient is null || !IsValidRecipient(recipient.Address) ||
            (recipient.DisplayName is not null &&
             (!EmailConfigurationRules.IsValidDisplayName(recipient.DisplayName) || recipient.DisplayName.Trim().Length > 100)))
            throw InvalidEmailInput();
    }

    private static bool IsValidRecipient(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 254 || address.Any(char.IsControl)) return false;
        try
        {
            var parsed = new MailAddress(address.Trim());
            return string.Equals(parsed.Address, address.Trim(), StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string DisplayName(EmailRecipient recipient) =>
        string.IsNullOrWhiteSpace(recipient.DisplayName) ? "bạn" : recipient.DisplayName.Trim();

    private static string Link(Uri link) => WebUtility.HtmlEncode(link.AbsoluteUri);

    private static BusinessException InvalidEmailInput() => new(
        "EMAIL_INVALID_INPUT",
        "Thông tin email không hợp lệ.",
        BusinessErrorKind.Validation);
}
