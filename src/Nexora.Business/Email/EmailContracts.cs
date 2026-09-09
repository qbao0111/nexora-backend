namespace Nexora.Business.Email;

public sealed record EmailRecipient(string Address, string? DisplayName = null);

public sealed record VerificationEmail(EmailRecipient Recipient, Uri VerificationLink);

public sealed record PasswordResetEmail(EmailRecipient Recipient, Uri ResetLink);

public sealed record ReminderEmail(EmailRecipient Recipient, string Summary, Uri? PracticeLink = null);

public interface IEmailSender
{
    Task SendVerificationAsync(VerificationEmail message, CancellationToken cancellationToken);
    Task SendPasswordResetAsync(PasswordResetEmail message, CancellationToken cancellationToken);
    Task SendReminderAsync(ReminderEmail message, CancellationToken cancellationToken);
}
