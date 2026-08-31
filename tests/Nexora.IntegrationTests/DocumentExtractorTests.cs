using System.IO.Compression;
using System.Text;
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

    private static void AddEntry(ZipArchive archive, string name, string value)
    {
        using var stream = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }
}
