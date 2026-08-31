using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Nexora.Business.Practice;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Nexora.Integrations.Storage;

/// <summary>
/// Extracts text from the document formats accepted by the upload provider.
/// OCR for image-only documents is intentionally out of scope for this adapter.
/// </summary>
public sealed class PdfDocxDocumentExtractor : IDocumentExtractor
{
    private const string PdfContentType = "application/pdf";
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        if (!content.CanRead || (content.CanSeek && content.Length == 0))
            throw new InvalidDataException("Document is empty.");
        if (!content.CanSeek)
            throw new InvalidDataException("Document stream must be seekable.");

        try
        {
            content.Position = 0;
            var text = contentType.Trim().ToLowerInvariant() switch
            {
                PdfContentType => ExtractPdf(content),
                DocxContentType => ExtractDocx(content),
                _ => throw new NotSupportedException("Only PDF and DOCX documents are supported.")
            };

            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("Document contains no extractable text.");

            return Task.FromResult(Normalize(text));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("Document could not be read.", exception);
        }
    }

    private static string ExtractPdf(Stream content)
    {
        using var document = PdfDocument.Open(content);
        return string.Join(Environment.NewLine, document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
    }

    private static string ExtractDocx(Stream content)
    {
        using var document = WordprocessingDocument.Open(content, false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidDataException("DOCX document has no body.");

        return string.Join(Environment.NewLine, body.Descendants<Paragraph>().Select(paragraph => paragraph.InnerText));
    }

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
