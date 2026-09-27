using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Business.Auth;
using Nexora.Business.Common;
using Nexora.Business.Email;
using Nexora.Business.Privacy;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.Data.Privacy;

public sealed partial class ExternalAccountDeletionService(
    NexoraDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IEmailSender emailSender,
    IOptions<EmailVerificationOptions> emailVerificationOptions,
    IPrivacyService privacyService,
    TimeProvider timeProvider,
    ILogger<ExternalAccountDeletionService> logger) : IExternalAccountDeletionService
{
    private static readonly TimeSpan ExternalVerificationLifetime = TimeSpan.FromMinutes(30);
    private readonly EmailVerificationOptions _emailVerificationOptions = emailVerificationOptions.Value;

    public async Task RequestExternalDeletionAsync(string email, CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await userManager.FindByEmailAsync(normalizedEmail);
        if (user is null || !user.IsActive || user.DeletionRequestedAt is not null || user.DeletedAt is not null ||
            string.IsNullOrWhiteSpace(user.Email))
            return;

        var now = timeProvider.GetUtcNow();
        var priorVerifications = await dbContext.ExternalDeletionVerifications
            .Where(item => item.UserId == user.Id).ToArrayAsync(cancellationToken);
        dbContext.ExternalDeletionVerifications.RemoveRange(priorVerifications.Where(item =>
            item.ConsumedAt is not null || item.ExpiresAt <= now));

        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verification = new ExternalDeletionVerification
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashExternalDeletionToken(rawToken),
            CreatedAt = now,
            ExpiresAt = now.Add(ExternalVerificationLifetime)
        };
        dbContext.ExternalDeletionVerifications.Add(verification);
        await dbContext.SaveChangesAsync(cancellationToken);

        var baseUrl = _emailVerificationOptions.PublicUrl.TrimEnd('/');
        var link = new Uri($"{baseUrl}/account-deletion/confirm?token={Uri.EscapeDataString(rawToken)}", UriKind.Absolute);
        try
        {
            await emailSender.SendAccountDeletionVerificationAsync(
                new AccountDeletionVerificationEmail(new EmailRecipient(user.Email), link), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ExternalDeletionEmailFailed(logger, user.Id);
        }
    }

    public async Task<DeletionRequestView> ConfirmExternalDeletionAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
            throw InvalidExternalDeletionVerification();

        var now = timeProvider.GetUtcNow();
        var tokenHash = HashExternalDeletionToken(token);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var verification = await dbContext.ExternalDeletionVerifications.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        if (verification is null || verification.ConsumedAt is not null || verification.ExpiresAt <= now)
            throw InvalidExternalDeletionVerification();

        var claimQuery = dbContext.ExternalDeletionVerifications
            .Where(item => item.Id == verification.Id && item.ConsumedAt == null);
        if (dbContext.Database.IsNpgsql())
            claimQuery = claimQuery.Where(item => item.ExpiresAt > now);
        var consumed = await claimQuery
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ConsumedAt, now), cancellationToken);
        if (consumed != 1) throw InvalidExternalDeletionVerification();

        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {verification.UserId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == verification.UserId, cancellationToken);
        if (user is null) throw InvalidExternalDeletionVerification();

        var current = await privacyService.GetCurrentDeletionRequestAsync(user.Id, cancellationToken);
        var request = current ?? await privacyService.RequestDeletionAsync(
            user.Id, $"external-deletion:{user.Id:N}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return request;
    }

    private static string HashExternalDeletionToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static BusinessException InvalidExternalDeletionVerification() =>
        new("DELETION_VERIFICATION_INVALID", "Liên kết xác minh không hợp lệ hoặc đã hết hạn.", BusinessErrorKind.Validation);

    [LoggerMessage(LogLevel.Warning, "Account deletion verification email could not be delivered for user {UserId}")]
    private static partial void ExternalDeletionEmailFailed(ILogger logger, Guid userId);
}
