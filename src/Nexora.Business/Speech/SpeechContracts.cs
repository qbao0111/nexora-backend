namespace Nexora.Business.Speech;

public sealed record SpeechAuthorizationToken(
    string Token,
    string Region,
    DateTimeOffset ExpiresAt);

public interface ISpeechTokenProvider
{
    Task<SpeechAuthorizationToken> GetAsync(CancellationToken cancellationToken);
}
