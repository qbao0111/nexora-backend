namespace Nexora.Api.Infrastructure;

internal static class AvatarUrls
{
    internal static string? For(Guid? avatarId) => avatarId is { } id ? $"/api/v1/avatars/{id:D}" : null;
}
