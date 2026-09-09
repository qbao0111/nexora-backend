using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Nexora.Business.Email;

namespace Nexora.IntegrationTests;

internal sealed class RecordingEmailSender : IEmailSender
{
    public Task SendVerificationAsync(VerificationEmail message, CancellationToken cancellationToken)
    {
        TestEmailInbox.Record(message.Recipient.Address, message.VerificationLink);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(PasswordResetEmail message, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendReminderAsync(ReminderEmail message, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class TestEmailInbox
{
    private static readonly ConcurrentDictionary<string, Uri> VerificationLinks = new(StringComparer.OrdinalIgnoreCase);

    public static void Record(string email, Uri verificationLink) => VerificationLinks[email] = verificationLink;

    public static Uri GetVerificationLink(string email) =>
        VerificationLinks.TryGetValue(email, out var link)
            ? link
            : throw new InvalidOperationException($"No verification email was recorded for {email}.");

    public static async Task VerifyAsync(HttpClient client, string email)
    {
        var link = GetVerificationLink(email);
        var query = link.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part.ElementAtOrDefault(1) ?? string.Empty), StringComparer.Ordinal);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new
        {
            userId = Guid.Parse(query["userId"]),
            token = query["token"]
        });
        if (response.StatusCode != HttpStatusCode.OK)
            throw new Xunit.Sdk.XunitException($"Email verification failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
