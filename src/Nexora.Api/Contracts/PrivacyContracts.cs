using System.ComponentModel.DataAnnotations;

namespace Nexora.Api.Contracts;

public sealed record DeletionRequestStatusResponse(
    Guid Id,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt);

public sealed record ExternalDeletionRequestAccepted(string Message);

public sealed record ExternalDeletionRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = string.Empty;
}

public sealed record ExternalDeletionConfirmation
{
    [Required, StringLength(128, MinimumLength = 1)]
    public string Token { get; init; } = string.Empty;
}
