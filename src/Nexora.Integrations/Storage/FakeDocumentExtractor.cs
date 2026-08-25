using Nexora.Business.Practice;

namespace Nexora.Integrations.Storage;

public sealed class FakeDocumentExtractor : IDocumentExtractor
{
    public Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!content.CanRead || content.Length == 0) throw new InvalidDataException("Document is empty.");
        return Task.FromResult($"Validated private resume document ({contentType}, {content.Length} bytes).");
    }
}
