namespace Nexora.Business.Auth;

public sealed record AvatarImage(Stream Content, string ContentType);

public interface IAvatarService
{
    Task<Guid> UploadAsync(Guid userId, Stream content, long length, string? contentType, CancellationToken cancellationToken);
    Task DeleteAsync(Guid userId, CancellationToken cancellationToken);
    Task<AvatarImage?> OpenAsync(Guid avatarId, CancellationToken cancellationToken);
}
