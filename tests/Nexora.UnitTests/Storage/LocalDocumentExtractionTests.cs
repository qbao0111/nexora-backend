using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Nexora.Business.Practice;
using Nexora.Integrations.Storage;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Nexora.UnitTests.Storage;

public sealed class LocalDocumentExtractionTests
{
    private const string TextContent = "Backend engineer at Example Company, 2022-2025. Skills: C#, PostgreSQL, automated tests. Education: Computer Science.";
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private readonly PdfDocxDocumentExtractor extractor = new();
    private static readonly JsonSerializerOptions HistoricalJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ReadablePdfUsesLocalTextExtraction()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(612, 792);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(TextContent, 10, new PdfPoint(30, 700), font);
        using var stream = new MemoryStream(builder.Build());

        var result = await extractor.ExtractDetailedAsync(stream, "application/pdf", CancellationToken.None);

        Assert.Equal(DocumentExtractionQuality.Good, result.Quality);
        Assert.Equal(DocumentExtractionMethod.PdfText, result.ExtractionMethod);
        Assert.Contains("Example Company", result.Text, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadableDocxUsesLocalOpenXmlExtraction()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text(TextContent)))));
            main.Document.Save();
        }

        var result = await extractor.ExtractDetailedAsync(stream, DocxType, CancellationToken.None);

        Assert.Equal(DocumentExtractionQuality.Good, result.Quality);
        Assert.Equal(DocumentExtractionMethod.DocxOpenXml, result.ExtractionMethod);
        Assert.Contains("2022-2025", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PdfWithoutTextFailsLocally()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(612, 792);
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=");
        page.AddPng(image, new PdfRectangle(30, 30, 580, 760));
        using var stream = new MemoryStream(builder.Build());

        var result = await extractor.ExtractDetailedAsync(stream, "application/pdf", CancellationToken.None);

        Assert.Equal(DocumentExtractionQuality.Failed, result.Quality);
        Assert.Empty(result.Text);
        Assert.Contains("TEXT_EXTRACTION_INSUFFICIENT", result.Warnings);
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractAsync(stream, "application/pdf", CancellationToken.None));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData(DocxType)]
    [InlineData("application/octet-stream")]
    public async Task InvalidOrUnsupportedDocumentFailsSafely(string contentType)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("private-invalid-document-content"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractDetailedAsync(stream, contentType, CancellationToken.None));

        Assert.DoesNotContain("private-invalid-document-content", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalGeminiExtractionMetadataRemainsReadable()
    {
        Assert.Equal(DocumentExtractionMethod.GeminiOcr, JsonSerializer.Deserialize<DocumentExtractionMethod>("\"GeminiOcr\"", HistoricalJsonOptions));
        Assert.Equal(DocumentExtractionMethod.GeminiOcr, JsonSerializer.Deserialize<DocumentExtractionMethod>("3"));
    }
}
