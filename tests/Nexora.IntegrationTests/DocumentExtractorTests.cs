using System.IO.Compression;
using System.Text;
using Nexora.Business.Practice;
using Nexora.Integrations.Storage;

namespace Nexora.IntegrationTests;

public sealed class DocumentExtractorTests
{
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Fact]
    public async Task ExtractsTextFromPdf()
    {
        using var content = new MemoryStream(PdfTestDocument.CreateTextPdf("Backend Developer CSharp PostgreSQL REST API"));
        var result = await new PdfDocxDocumentExtractor().ExtractAsync(content, "application/pdf", CancellationToken.None);

        Assert.Contains("Backend Developer", result, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsExtractionQualityMetrics()
    {
        using var content = new MemoryStream(PdfTestDocument.CreateTextPdf(
            "Backend Developer built reliable CSharp .NET services with PostgreSQL and REST API testing"));
        var result = await new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, "application/pdf", CancellationToken.None);

        Assert.Equal(DocumentExtractionMethod.PdfText, result.ExtractionMethod);
        Assert.Equal(DocumentExtractionQuality.Good, result.Quality);
        Assert.Equal(1, result.PageCount);
        Assert.True(result.CharacterCount > 40);
        Assert.True(result.WordCount >= 10);
        Assert.InRange(result.QualityScore, 0.75, 1);
        Assert.True(result.PrintableCharacterRatio > 0.99);
    }

    [Fact]
    public async Task ReconstructsLikelyMultiColumnPdfFromWordPositions()
    {
        var words = new List<(string Text, double X, double Y)>();
        var left = new[] { "Backend", "CSharp", "PostgreSQL", "Nexora", "Experience", "Education" };
        var right = new[] { "Developer", ".NET", "REST", "Projects", "2024", "Computer" };
        for (var index = 0; index < left.Length; index++)
        {
            words.Add((left[index], 72, 720 - index * 24));
            words.Add((right[index], 360, 720 - index * 24));
        }

        using var content = new MemoryStream(PdfTestDocument.CreatePositionedTextPdf(words.ToArray()));
        var result = await new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, "application/pdf", CancellationToken.None);

        Assert.Equal(DocumentExtractionMethod.PdfLayoutReconstructed, result.ExtractionMethod);
        Assert.Contains("Backend", result.Text, StringComparison.Ordinal);
        Assert.Contains("Projects", result.Text, StringComparison.Ordinal);
        Assert.Contains("LAYOUT_RECONSTRUCTION_ATTEMPTED", result.Warnings);
    }

    [Fact]
    public async Task RemovesNullCharactersFromPdfText()
    {
        using var content = new MemoryStream(PdfTestDocument.CreateTextPdf("Backend\0Developer PostgreSQL REST API"));
        var result = await new PdfDocxDocumentExtractor().ExtractAsync(content, "application/pdf", CancellationToken.None);

        Assert.DoesNotContain('\0', result);
        Assert.Contains("Backend", result, StringComparison.Ordinal);
        Assert.Contains("Developer", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractsTextFromDocx()
    {
        using var content = CreateDocx("Backend Developer CSharp PostgreSQL REST API");
        var result = await new PdfDocxDocumentExtractor().ExtractAsync(content, DocxContentType, CancellationToken.None);

        Assert.Contains("Backend Developer", result, StringComparison.Ordinal);
        Assert.Contains("REST API", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractsDocxTablesInDocumentOrder()
    {
        using var content = CreateDocxWithTable();
        var result = await new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, DocxContentType, CancellationToken.None);

        Assert.Equal(DocumentExtractionMethod.DocxOpenXml, result.ExtractionMethod);
        Assert.Contains("Backend Developer", result.Text, StringComparison.Ordinal);
        Assert.Contains("Skills | CSharp, .NET", result.Text, StringComparison.Ordinal);
        Assert.True(result.Text.IndexOf("Experience", StringComparison.Ordinal) < result.Text.IndexOf("Skills", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreservesUnicodeAndMixedLanguageDocxText()
    {
        using var content = CreateDocx("Kỹ sư phần mềm Backend — CSharp .NET, PostgreSQL và REST API");
        var result = await new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, DocxContentType, CancellationToken.None);

        Assert.Contains("Kỹ sư phần mềm", result.Text, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", result.Text, StringComparison.Ordinal);
        Assert.Equal(DocumentExtractionQuality.Good, result.Quality);
    }

    [Fact]
    public async Task RemovesRepeatedPageBoundariesAndDecorativeNoise()
    {
        using var content = new MemoryStream(PdfTestDocument.CreateTextPdfPages(
            "NEXORA CV\nBackend Developer CSharp PostgreSQL REST API\n---\n1",
            "NEXORA CV\nNexora Engineer .NET REST API testing\n---\n2"));
        var result = await new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, "application/pdf", CancellationToken.None);

        Assert.Equal(2, result.PageCount);
        Assert.DoesNotContain("NEXORA CV", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("---", result.Text, StringComparison.Ordinal);
        Assert.Contains("REPEATED_HEADER_FOOTER_REMOVED", result.Warnings);
        Assert.Contains("DECORATIVE_SEPARATOR_REMOVED", result.Warnings);
    }

    [Fact]
    public async Task EmptyPdfIsMarkedFailedAndReportsOcrBoundary()
    {
        using var content = new MemoryStream(PdfTestDocument.CreateTextPdf(string.Empty));
        var result = await new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, "application/pdf", CancellationToken.None);

        Assert.Equal(DocumentExtractionQuality.Failed, result.Quality);
        Assert.Contains("OCR_MAY_BE_REQUIRED", result.Warnings);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PdfDocxDocumentExtractor().ExtractAsync(content, "application/pdf", CancellationToken.None));
    }

    [Fact]
    public async Task ImageOnlyPdfIsMarkedSuspiciousWithoutOcrInvocation()
    {
        using var content = new MemoryStream(PdfTestDocument.CreateImageOnlyPdf());
        var result = await new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, "application/pdf", CancellationToken.None);

        Assert.NotEqual(DocumentExtractionQuality.Good, result.Quality);
        Assert.Contains("OCR_MAY_BE_REQUIRED", result.Warnings);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task MalformedDocumentFailsWithoutOcrFallback()
    {
        using var content = new MemoryStream([1, 2, 3, 4]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, "application/pdf", CancellationToken.None));
    }

    [Fact]
    public async Task HonorsCancellationBeforeExtraction()
    {
        using var content = new MemoryStream(PdfTestDocument.CreateTextPdf("Backend Developer PostgreSQL REST API"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new PdfDocxDocumentExtractor().ExtractDetailedAsync(content, "application/pdf", cancellation.Token));
    }

    private static MemoryStream CreateDocx(string text)
    {
        var content = new MemoryStream();
        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, true))
        {
            AddEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
                  <Default Extension="xml" ContentType="application/xml" />
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
                </Types>
                """);
            AddEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml" />
                </Relationships>
                """);
            AddEntry(archive, "word/document.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p><w:sectPr /></w:body>
                </w:document>
                """);
        }

        content.Position = 0;
        return content;
    }

    private static MemoryStream CreateDocxWithTable()
    {
        var content = new MemoryStream();
        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, true))
        {
            AddEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
                  <Default Extension="xml" ContentType="application/xml" />
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
                </Types>
                """);
            AddEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml" />
                </Relationships>
                """);
            AddEntry(archive, "word/document.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p><w:r><w:t>Backend Developer</w:t></w:r></w:p>
                    <w:tbl>
                      <w:tr><w:tc><w:p><w:r><w:t>Experience</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Nexora</w:t></w:r></w:p></w:tc></w:tr>
                      <w:tr><w:tc><w:p><w:r><w:t>Skills</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>CSharp, .NET</w:t></w:r></w:p></w:tc></w:tr>
                    </w:tbl>
                    <w:sectPr />
                  </w:body>
                </w:document>
                """);
        }

        content.Position = 0;
        return content;
    }

    private static void AddEntry(ZipArchive archive, string name, string value)
    {
        using var stream = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }
}
