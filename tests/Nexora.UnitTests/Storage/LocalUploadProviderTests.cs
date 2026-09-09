using System.IO.Compression;
using System.Text;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Storage;
using Nexora.Integrations.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Writer;

namespace Nexora.UnitTests.Storage;

public sealed class LocalUploadProviderTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string PdfType = "application/pdf";
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Fact]
    public async Task CreateIntentSplitsZeroOversizeAndUnsupportedErrors()
    {
        var provider = CreateProvider();

        var zero = await Assert.ThrowsAsync<BusinessException>(() => provider.CreateIntentAsync(UserId, "cv.pdf", PdfType, 0, CancellationToken.None));
        var large = await Assert.ThrowsAsync<BusinessException>(() => provider.CreateIntentAsync(UserId, "cv.pdf", PdfType, 10 * 1024 * 1024 + 1, CancellationToken.None));
        var unsupported = await Assert.ThrowsAsync<BusinessException>(() => provider.CreateIntentAsync(UserId, "cv.txt", "text/plain", 10, CancellationToken.None));

        Assert.Equal("UPLOAD_SIZE_ZERO", zero.Code);
        Assert.Equal("UPLOAD_SIZE_EXCEEDED", large.Code);
        Assert.Equal("UPLOAD_TYPE_UNSUPPORTED", unsupported.Code);
    }

    [Fact]
    public async Task UploadHandlesChunkedStreamAndRejectsSizeMismatchAndCorruptPdf()
    {
        var storage = new RecordingStorage();
        var provider = CreateProvider(storage);
        var bytes = ValidPdf();
        var intent = await provider.CreateIntentAsync(UserId, "cv.pdf", PdfType, bytes.Length, CancellationToken.None);

        await provider.UploadAsync(intent.Token, new ChunkedReadStream(bytes, 3), CancellationToken.None);
        var completed = await provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None);
        Assert.Equal(bytes.Length, completed.Size);
        Assert.Equal(1, storage.SaveCalls);

        var shortBytes = bytes[..^1];
        var mismatchIntent = await provider.CreateIntentAsync(UserId, "short.pdf", PdfType, bytes.Length, CancellationToken.None);
        var mismatch = await Assert.ThrowsAsync<BusinessException>(() => provider.UploadAsync(
            mismatchIntent.Token, new MemoryStream(shortBytes), CancellationToken.None));
        Assert.Equal("UPLOAD_SIZE_MISMATCH", mismatch.Code);

        var corrupt = Encoding.ASCII.GetBytes("%PDF-1.7\nnot a PDF container");
        var corruptIntent = await provider.CreateIntentAsync(UserId, "corrupt.pdf", PdfType, corrupt.Length, CancellationToken.None);
        var invalidContainer = await Assert.ThrowsAsync<BusinessException>(() => provider.UploadAsync(
            corruptIntent.Token, new MemoryStream(corrupt), CancellationToken.None));
        Assert.Equal("UPLOAD_CONTAINER_INVALID", invalidContainer.Code);
    }

    [Fact]
    public async Task UploadRequiresRealPdfOrDocxContainer()
    {
        var storage = new RecordingStorage();
        var provider = CreateProvider(storage);
        var fakeZip = CreateZip(("junk.txt", Encoding.UTF8.GetBytes("not a docx")));
        var fakeIntent = await provider.CreateIntentAsync(UserId, "fake.docx", DocxType, fakeZip.Length, CancellationToken.None);
        var fake = await Assert.ThrowsAsync<BusinessException>(() => provider.UploadAsync(
            fakeIntent.Token, new MemoryStream(fakeZip), CancellationToken.None));
        Assert.Equal("UPLOAD_CONTAINER_INVALID", fake.Code);

        var docx = ValidDocx();
        var docxIntent = await provider.CreateIntentAsync(UserId, "real.docx", DocxType, docx.Length, CancellationToken.None);
        await provider.UploadAsync(docxIntent.Token, new ChunkedReadStream(docx, 7), CancellationToken.None);
        Assert.Equal(docx.Length, (await provider.GetCompletedAsync(UserId, docxIntent.Token, CancellationToken.None)).Size);
    }

    [Fact]
    public async Task UploadAcceptsValidEncryptedPdfForExtractionStage()
    {
        var storage = new RecordingStorage();
        var provider = CreateProvider(storage);
        var bytes = EncryptedPdf();
        var intent = await provider.CreateIntentAsync(UserId, "protected.pdf", PdfType, bytes.Length, CancellationToken.None);

        Assert.Throws<PdfDocumentEncryptedException>(() =>
        {
            using var document = PdfDocument.Open(new MemoryStream(bytes, writable: false));
        });

        await provider.UploadAsync(intent.Token, new MemoryStream(bytes), CancellationToken.None);

        var completed = await provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None);
        Assert.Equal(bytes.Length, completed.Size);
        Assert.Equal(1, storage.SaveCalls);
    }

    [Fact]
    public async Task IntentIsSingleUseUnderConcurrentPutAndExpires()
    {
        var storage = new RecordingStorage();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var provider = CreateProvider(storage, clock);
        var bytes = ValidPdf();
        var intent = await provider.CreateIntentAsync(UserId, "cv.pdf", PdfType, bytes.Length, CancellationToken.None);

        var first = provider.UploadAsync(intent.Token, new MemoryStream(bytes), CancellationToken.None);
        var second = provider.UploadAsync(intent.Token, new MemoryStream(bytes), CancellationToken.None);
        var concurrent = await Record.ExceptionAsync(async () => await Task.WhenAll(first, second));

        Assert.NotNull(concurrent);
        Assert.Equal(1, storage.SaveCalls);

        var expired = await provider.CreateIntentAsync(UserId, "expired.pdf", PdfType, bytes.Length, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(11));
        var expiredException = await Assert.ThrowsAsync<BusinessException>(() => provider.UploadAsync(
            expired.Token, new MemoryStream(bytes), CancellationToken.None));
        Assert.Equal("UPLOAD_INTENT_INVALID", expiredException.Code);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("   \r\n")]
    public async Task UploadAcceptsPdfWithLeadingWhitespacePreamble(string preamble)
    {
        var storage = new RecordingStorage();
        var provider = CreateProvider(storage);
        var preambleBytes = Encoding.ASCII.GetBytes(preamble);
        var validPdf = ValidPdf();
        var bytes = preambleBytes.Concat(validPdf).ToArray();

        var intent = await provider.CreateIntentAsync(UserId, "cv_online.pdf", PdfType, bytes.Length, CancellationToken.None);
        await provider.UploadAsync(intent.Token, new MemoryStream(bytes), CancellationToken.None);

        var completed = await provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None);
        Assert.Equal(bytes.Length, completed.Size);
        Assert.Equal(1, storage.SaveCalls);
    }

    [Fact]
    public async Task UploadAcceptsPdfWithLeadingUtf8Bom()
    {
        var storage = new RecordingStorage();
        var provider = CreateProvider(storage);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var validPdf = ValidPdf();
        var bytes = bom.Concat(validPdf).ToArray();

        var intent = await provider.CreateIntentAsync(UserId, "bom.pdf", PdfType, bytes.Length, CancellationToken.None);
        await provider.UploadAsync(intent.Token, new MemoryStream(bytes), CancellationToken.None);

        var completed = await provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None);
        Assert.Equal(bytes.Length, completed.Size);
        Assert.Equal(1, storage.SaveCalls);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task UploadAcceptsDocxWithLeadingWhitespacePreamble(string preamble)
    {
        var storage = new RecordingStorage();
        var provider = CreateProvider(storage);
        var preambleBytes = Encoding.ASCII.GetBytes(preamble);
        var validDocx = ValidDocx();
        var bytes = preambleBytes.Concat(validDocx).ToArray();

        var intent = await provider.CreateIntentAsync(UserId, "cv_online.docx", DocxType, bytes.Length, CancellationToken.None);
        await provider.UploadAsync(intent.Token, new MemoryStream(bytes), CancellationToken.None);

        var completed = await provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None);
        Assert.Equal(bytes.Length, completed.Size);
        Assert.Equal(1, storage.SaveCalls);
    }

    private static LocalUploadProvider CreateProvider(RecordingStorage? storage = null, TimeProvider? clock = null) =>
        new(
            storage ?? new RecordingStorage(),
            Options.Create(new UploadOptions { MaxResumeBytes = 10 * 1024 * 1024, IntentMinutes = 10 }),
            clock ?? TimeProvider.System,
            NullLogger<LocalUploadProvider>.Instance);

    private static byte[] ValidPdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(612, 792);
        return builder.Build();
    }

    private static byte[] ValidDocx()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("A real DOCX fixture.")))));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] EncryptedPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Filter /Standard /V 1 /R 2 /Length 40 /O (01234567890123456789012345678901) /U (01234567890123456789012345678901) /P -4 >>"
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length + 1];
        for (var index = 0; index < objects.Length; index++)
        {
            offsets[index + 1] = builder.Length;
            builder.Append(index + 1).Append(" 0 obj\n")
                .Append(objects[index]).Append("\nendobj\n");
        }

        var xrefOffset = builder.Length;
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        for (var index = 1; index < offsets.Length; index++)
            builder.Append(offsets[index].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1)
            .Append(" /Root 1 0 R /Encrypt 4 0 R /ID [<0123456789ABCDEF> <0123456789ABCDEF>] >>\n")
            .Append("startxref\n").Append(xrefOffset.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] CreateZip((string Name, byte[] Content) entry)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var target = new StreamWriter(archive.CreateEntry(entry.Name).Open(), Encoding.UTF8);
            target.Write(Encoding.UTF8.GetString(entry.Content));
        }
        return stream.ToArray();
    }

    private sealed class RecordingStorage : IStorageProvider
    {
        private int saveCalls;
        public int SaveCalls => saveCalls;

        public async Task<StoredObject> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref saveCalls);
            await using var sink = new MemoryStream();
            await content.CopyToAsync(sink, cancellationToken);
            return new StoredObject($"stored/{Guid.NewGuid():N}.bin", fileName, contentType, sink.Length);
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ChunkedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(chunkSize, buffer.Length)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunkSize, buffer.Length)], cancellationToken);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }
}
