using Nexora.Business.Email;

namespace Nexora.Integrations.Email;

public sealed class NoOpEmailSender : IEmailSender
{
    public Task SendVerificationAsync(VerificationEmail message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(PasswordResetEmail message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SendReminderAsync(ReminderEmail message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
