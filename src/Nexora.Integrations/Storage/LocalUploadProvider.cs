using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace Nexora.Integrations.Storage;

public sealed class UploadOptions
{
    public const string SectionName = "Uploads";
    public long MaxResumeBytes { get; set; } = 10 * 1024 * 1024;
    public int IntentMinutes { get; set; } = 10;
}

public sealed partial class LocalUploadProvider(
    IStorageProvider storageProvider,
    IOptions<UploadOptions> options,
    TimeProvider timeProvider,
    ILogger<LocalUploadProvider>? logger = null) : IUploadProvider
{
    private const long HardMaximumResumeBytes = 25 * 1024 * 1024;
    private const int ReadBufferSize = 80 * 1024;
    private const int MaximumDocxEntries = 256;
    private const long MaximumDocxEntryBytes = 8 * 1024 * 1024;
    private const long MaximumDocxDecompressedBytes = 25 * 1024 * 1024;
    private static readonly TimeSpan MaximumDocxValidationTime = TimeSpan.FromSeconds(2);

    private readonly ILogger<LocalUploadProvider> logger = logger ?? NullLogger<LocalUploadProvider>.Instance;

    private sealed class Intent(
        Guid userId,
        string fileName,
        string contentType,
        long size,
        DateTimeOffset expiresAt)
    {
        public Guid UserId { get; } = userId;
        public string FileName { get; } = fileName;
        public string ContentType { get; } = contentType;
        public long Size { get; } = size;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public PendingUpload? Completed { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Intent> intents = new(StringComparer.Ordinal);

    public Task<UploadIntent> CreateIntentAsync(
        Guid userId,
        string fileName,
        string contentType,
        long size,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedFileName = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        var normalizedType = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
        var maxBytes = GetMaximumResumeBytes();
        var extension = Path.GetExtension(normalizedFileName).ToLowerInvariant();

        if (size == 0)
            throw Reject("UPLOAD_SIZE_ZERO", "File CV không được để trống.", size, size, maxBytes, normalizedType, "unknown", "zero_size");
        if (size < 0 || size > maxBytes)
            throw Reject("UPLOAD_SIZE_EXCEEDED", "Dung lượng file CV vượt quá giới hạn cho phép.", size, size, maxBytes, normalizedType, "unknown", "size_limit");
        if (!IsSupportedPair(extension, normalizedType) || string.IsNullOrWhiteSpace(normalizedFileName))
            throw Reject("UPLOAD_TYPE_UNSUPPORTED", "Chỉ chấp nhận file PDF hoặc DOCX với MIME tương ứng.", size, size, maxBytes, normalizedType, "unknown", "type_or_extension");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(options.Value.IntentMinutes);
        intents[token] = new Intent(userId, normalizedFileName, normalizedType, size, expiresAt);
        return Task.FromResult(new UploadIntent(token, $"/api/v1/uploads/{token}", expiresAt));
    }

    public async Task UploadAsync(string token, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var maxBytes = GetMaximumResumeBytes();

        if (!intents.TryGetValue(token, out var intent))
            throw Reject("UPLOAD_INTENT_INVALID", "Upload intent không hợp lệ hoặc đã hết hạn.", -1, -1, maxBytes, "unknown", "unknown", "intent_missing");

        await intent.Gate.WaitAsync(cancellationToken);
        try
        {
            if (intent.ExpiresAt <= timeProvider.GetUtcNow() || intent.Completed is not null)
                throw Reject("UPLOAD_INTENT_INVALID", "Upload intent không hợp lệ hoặc đã hết hạn.", intent.Size, -1, maxBytes, intent.ContentType, "unknown", "intent_expired_or_used");

            var bytes = await ReadExactlyBoundedAsync(content, intent.Size, cancellationToken);
            if (bytes.Length != intent.Size)
            {
                var detected = DetectContentType(bytes);
                throw Reject("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.", intent.Size, bytes.Length, maxBytes, intent.ContentType, detected, "body_short");
            }

            var extra = await ReadOneByteAsync(content, cancellationToken);
            if (extra > 0)
            {
                var detected = DetectContentType(bytes);
                throw Reject("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.", intent.Size, intent.Size + extra, maxBytes, intent.ContentType, detected, "body_long");
            }

            var detectedType = DetectContentType(bytes);
            if (!HasSignature(bytes, intent.ContentType))
                throw Reject("UPLOAD_SIGNATURE_INVALID", "Nội dung file không khớp với MIME đã khai báo.", intent.Size, bytes.Length, maxBytes, intent.ContentType, detectedType, "signature");

            var containerResult = await ValidateContainerAsync(bytes, intent.ContentType, cancellationToken);
            if (containerResult is not null)
                throw Reject(containerResult.Code, containerResult.Message, intent.Size, bytes.Length, maxBytes, intent.ContentType, detectedType, containerResult.Reason);

            var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await using var source = new MemoryStream(bytes, writable: false);
            var stored = await storageProvider.SaveAsync(source, intent.FileName, intent.ContentType, cancellationToken);
            if (stored.Size != intent.Size)
            {
                await TryDeleteAsync(stored.StorageKey, cancellationToken);
                throw Reject("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.", intent.Size, stored.Size, maxBytes, intent.ContentType, detectedType, "stored_size");
            }

            // The per-intent gate makes the capability single-use even when two PUTs
            // arrive concurrently; only the winning upload can create a stored object.
            intent.Completed = new PendingUpload(
                token,
                intent.UserId,
                stored.StorageKey,
                stored.FileName,
                stored.ContentType,
                stored.Size,
                checksum);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogUploadRejected(logger, intent.Size, -1, maxBytes, intent.ContentType, "unknown", "cancelled");
            throw;
        }
        finally
        {
            intent.Gate.Release();
        }
    }

    public Task<PendingUpload> GetCompletedAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!intents.TryGetValue(token, out var intent) || intent.UserId != userId || intent.Completed is null)
            throw new BusinessException("UPLOAD_NOT_FOUND", "Không tìm thấy file upload hợp lệ.", BusinessErrorKind.NotFound);
        return Task.FromResult(intent.Completed);
    }

    private async Task TryDeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        try
        {
            await storageProvider.DeleteAsync(storageKey, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStoredObjectCleanupFailed(logger, exception.GetType().Name);
        }
    }

    private static async Task<byte[]> ReadExactlyBoundedAsync(Stream content, long expectedSize, CancellationToken cancellationToken)
    {
        if (expectedSize <= 0 || expectedSize > HardMaximumResumeBytes)
            return [];

        await using var buffered = new MemoryStream(capacity: checked((int)Math.Min(expectedSize, int.MaxValue)));
        var buffer = new byte[ReadBufferSize];
        var remaining = expectedSize;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readLength = (int)Math.Min(buffer.Length, remaining);
            var read = await content.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken);
            if (read == 0) break;
            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        return buffered.ToArray();
    }

    private static async Task<int> ReadOneByteAsync(Stream content, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        return await content.ReadAsync(one.AsMemory(), cancellationToken);
    }

    private static bool IsSupportedPair(string extension, string contentType) =>
        (extension, contentType) is
            (".pdf", "application/pdf") or
            (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

    private static bool HasSignature(ReadOnlySpan<byte> content, string contentType) => contentType switch
    {
        "application/pdf" => content.StartsWith("%PDF-"u8),
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => IsZipSignature(content),
        _ => false
    };

    private static string DetectContentType(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("%PDF-"u8)) return "application/pdf";
        if (IsZipSignature(content)) return "application/zip";
        return "unknown";
    }

    private static bool IsZipSignature(ReadOnlySpan<byte> content) =>
        content.Length >= 4 &&
        content[0] == 0x50 && content[1] == 0x4B &&
        content[2] == 0x03 && content[3] == 0x04;

    private static async Task<ContainerFailure?> ValidateContainerAsync(
        byte[] content,
        string contentType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (contentType == "application/pdf")
        {
            try
            {
                using var stream = new MemoryStream(content, writable: false);
                using var document = PdfDocument.Open(stream);
                cancellationToken.ThrowIfCancellationRequested();
                return document.GetPages().Any()
                    ? null
                    : new ContainerFailure("UPLOAD_CONTAINER_INVALID", "PDF container không có trang hợp lệ.", "pdf_no_pages");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PdfDocumentEncryptedException)
            {
                // Encryption is a readable-container concern, not an upload-format failure.
                // Let the extraction boundary decide whether OCR can process the protected file.
                return null;
            }
            catch (Exception exception)
            {
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "PDF container không hợp lệ.", $"pdf_parse_{exception.GetType().Name}");
            }
        }

        if (contentType != "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
            return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "Container tài liệu không được hỗ trợ.", "content_type");

        return await ValidateDocxContainerAsync(content, cancellationToken);
    }

    private static async Task<ContainerFailure?> ValidateDocxContainerAsync(byte[] content, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var source = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumDocxEntries)
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không hợp lệ.", "docx_entry_count");

            long decompressedBytes = 0;
            byte[]? contentTypes = null;
            byte[]? documentXml = null;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed > MaximumDocxValidationTime)
                    return new ContainerFailure("UPLOAD_CONTAINER_LIMIT", "DOCX container vượt giới hạn xử lý.", "docx_validation_timeout");
                if (entry.FullName.Length > 512 || IsUnsafeZipPath(entry.FullName))
                    return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không hợp lệ.", "docx_entry_path");
                if (entry.Length < 0 || entry.Length > MaximumDocxEntryBytes)
                    return new ContainerFailure("UPLOAD_CONTAINER_LIMIT", "DOCX container vượt giới hạn kích thước giải nén.", "docx_entry_size");
                if (entry.FullName.EndsWith('/')) continue;

                var entryResult = await ReadZipEntryAsync(entry, stopwatch, decompressedBytes, cancellationToken);
                decompressedBytes = entryResult.DecompressedBytes;
                var entryBytes = entryResult.Bytes;
                if (entry.FullName.Equals("[Content_Types].xml", StringComparison.Ordinal))
                    contentTypes = entryBytes;
                else if (entry.FullName.Equals("word/document.xml", StringComparison.Ordinal))
                    documentXml = entryBytes;
            }

            if (contentTypes is null || documentXml is null)
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container thiếu thành phần OpenXML bắt buộc.", "docx_required_entries");
            if (!IsSafeXml(contentTypes) || !IsSafeXml(documentXml))
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container chứa XML không hợp lệ.", "docx_xml");

            cancellationToken.ThrowIfCancellationRequested();
            await using var packageStream = new MemoryStream(content, writable: false);
            using var document = WordprocessingDocument.Open(packageStream, false);
            if (document.MainDocumentPart?.Document?.Body is null)
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không có nội dung tài liệu.", "docx_body");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không hợp lệ.", "docx_zip");
        }
        catch (Exception exception)
        {
            return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không hợp lệ.", $"docx_parse_{exception.GetType().Name}");
        }
    }

    private static async Task<(byte[] Bytes, long DecompressedBytes)> ReadZipEntryAsync(
        ZipArchiveEntry entry,
        Stopwatch stopwatch,
        long decompressedBytes,
        CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        await using var output = new MemoryStream(capacity: checked((int)Math.Min(entry.Length, MaximumDocxEntryBytes)));
        var buffer = new byte[ReadBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > MaximumDocxValidationTime)
                throw new InvalidDataException("DOCX validation took too long.");
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            decompressedBytes = checked(decompressedBytes + read);
            if (decompressedBytes > MaximumDocxDecompressedBytes || output.Length + read > MaximumDocxEntryBytes)
                throw new InvalidDataException("DOCX decompressed content is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return (output.ToArray(), decompressedBytes);
    }

    private static bool IsUnsafeZipPath(string path)
    {
        if ((path.Length > 0 && path[0] == '/') || path.Contains(':', StringComparison.Ordinal)) return true;
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..");
    }

    private static bool IsSafeXml(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024,
            MaxCharactersFromEntities = 0
        };
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read()) { }
        return true;
    }

    private long GetMaximumResumeBytes() =>
        options.Value.MaxResumeBytes is > 0 and <= HardMaximumResumeBytes
            ? options.Value.MaxResumeBytes
            : throw new BusinessException("UPLOAD_CONFIGURATION_INVALID", "Giới hạn upload chưa được cấu hình hợp lệ.", BusinessErrorKind.ExternalFailure);

    private BusinessException Reject(
        string code,
        string message,
        long expectedSize,
        long actualSize,
        long maxBytes,
        string declaredType,
        string detectedType,
        string reason)
    {
        LogUploadRejected(logger, expectedSize, actualSize, maxBytes, declaredType, detectedType, reason);
        return new BusinessException(code, message, code == "UPLOAD_INTENT_INVALID" ? BusinessErrorKind.NotFound : BusinessErrorKind.Validation);
    }

    [LoggerMessage(LogLevel.Warning, "Upload rejected: expectedBytes={ExpectedBytes} actualBytes={ActualBytes} maxBytes={MaxBytes} declaredType={DeclaredType} detectedType={DetectedType} reason={Reason}")]
    private static partial void LogUploadRejected(
        ILogger logger,
        long expectedBytes,
        long actualBytes,
        long maxBytes,
        string declaredType,
        string detectedType,
        string reason);

    [LoggerMessage(LogLevel.Warning, "Stored upload cleanup failed after validation: exceptionType={ExceptionType}")]
    private static partial void LogStoredObjectCleanupFailed(ILogger logger, string exceptionType);

    private sealed record ContainerFailure(string Code, string Message, string Reason);
}
