using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace Nexora.Integrations.Storage;

public sealed record NormalizedUploadRequest(string FileName, string ContentType, long Size);

public sealed record ValidatedUpload(byte[] Content, string DetectedContentType, string Checksum)
{
    public long Size => Content.LongLength;
}

/// <summary>
/// Validates upload metadata and the actual PDF/DOCX bytes. It is shared by the
/// local and R2 upload paths so a provider cannot make an untrusted object usable.
/// </summary>
public sealed class UploadDocumentValidator(IOptions<UploadOptions> options)
{
    private const long HardMaximumResumeBytes = 25 * 1024 * 1024;
    private const int ReadBufferSize = 80 * 1024;
    private const int MaximumDocxEntries = 256;
    private const long MaximumDocxEntryBytes = 8 * 1024 * 1024;
    private const long MaximumDocxDecompressedBytes = 25 * 1024 * 1024;
    private static readonly TimeSpan MaximumDocxValidationTime = TimeSpan.FromSeconds(2);

    public NormalizedUploadRequest ValidateRequest(string fileName, string contentType, long size)
    {
        var normalizedFileName = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        var normalizedType = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
        var maxBytes = GetMaximumResumeBytes();
        var extension = Path.GetExtension(normalizedFileName).ToLowerInvariant();

        if (size == 0)
            throw Invalid("UPLOAD_SIZE_ZERO", "File CV không được để trống.");
        if (size < 0 || size > maxBytes)
            throw Invalid("UPLOAD_SIZE_EXCEEDED", "Dung lượng file CV vượt quá giới hạn cho phép.");
        if (string.IsNullOrWhiteSpace(normalizedFileName) || !IsSupportedPair(extension, normalizedType))
            throw Invalid("UPLOAD_TYPE_UNSUPPORTED", "Chỉ chấp nhận file PDF hoặc DOCX với MIME tương ứng.");

        return new NormalizedUploadRequest(normalizedFileName, normalizedType, size);
    }

    public async Task<ValidatedUpload> ValidateAsync(
        Stream content,
        NormalizedUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var maxBytes = GetMaximumResumeBytes();
        var bytes = await ReadExactlyBoundedAsync(content, request.Size, cancellationToken);
        if (bytes.LongLength != request.Size)
            throw Invalid("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.");

        var extra = await ReadOneByteAsync(content, cancellationToken);
        if (extra > 0)
            throw Invalid("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.");

        var detectedType = DetectContentType(bytes);
        if (!HasSignature(bytes, request.ContentType))
            throw Invalid("UPLOAD_SIGNATURE_INVALID", "Nội dung file không khớp với MIME đã khai báo.");

        var containerFailure = await ValidateContainerAsync(bytes, request.ContentType, cancellationToken);
        if (containerFailure is not null)
            throw Invalid(containerFailure.Code, containerFailure.Message);

        return new ValidatedUpload(bytes, detectedType, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static BusinessException Invalid(string code, string message) =>
        new(code, message, code == "UPLOAD_INTENT_INVALID" ? BusinessErrorKind.NotFound : BusinessErrorKind.Validation);

    private long GetMaximumResumeBytes() =>
        options.Value.MaxResumeBytes is > 0 and <= HardMaximumResumeBytes
            ? options.Value.MaxResumeBytes
            : throw new BusinessException("UPLOAD_CONFIGURATION_INVALID", "Giới hạn upload chưa được cấu hình hợp lệ.", BusinessErrorKind.ExternalFailure);

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

    private static ReadOnlySpan<byte> SkipLeadingPreamble(ReadOnlySpan<byte> content)
    {
        var slice = content;
        if (slice.Length >= 3 && slice[0] == 0xEF && slice[1] == 0xBB && slice[2] == 0xBF)
            slice = slice[3..];

        while (slice.Length > 0 && slice[0] is (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)' ')
            slice = slice[1..];

        return slice;
    }

    private static bool HasSignature(ReadOnlySpan<byte> content, string contentType) => contentType switch
    {
        "application/pdf" => HasPdfSignature(content),
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => IsZipSignature(content),
        _ => false
    };

    private static bool HasPdfSignature(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty) return false;
        var trimmed = SkipLeadingPreamble(content);
        if (trimmed.StartsWith("%PDF-"u8)) return true;

        var window = content[..Math.Min(content.Length, 1024)];
        var index = window.IndexOf("%PDF-"u8);
        if (index <= 0) return false;

        var preamble = window[..index];
        foreach (var value in preamble)
        {
            if (value is not ((byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)' ' or 0xEF or 0xBB or 0xBF or (byte)'%'))
                return false;
        }

        return true;
    }

    private static string DetectContentType(ReadOnlySpan<byte> content)
    {
        if (HasPdfSignature(content)) return "application/pdf";
        if (IsZipSignature(content)) return "application/zip";
        return "unknown";
    }

    private static bool IsZipSignature(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty) return false;
        var trimmed = SkipLeadingPreamble(content);
        return trimmed.Length >= 4 &&
               trimmed[0] == 0x50 && trimmed[1] == 0x4B &&
               trimmed[2] == 0x03 && trimmed[3] == 0x04;
    }

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
                    : new ContainerFailure("UPLOAD_CONTAINER_INVALID", "PDF container không có trang hợp lệ.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PdfDocumentEncryptedException)
            {
                return null;
            }
            catch
            {
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "PDF container không hợp lệ.");
            }
        }

        return await ValidateDocxContainerAsync(content, cancellationToken);
    }

    private static int GetPreambleLength(ReadOnlySpan<byte> content) =>
        content.Length - SkipLeadingPreamble(content).Length;

    private static async Task<ContainerFailure?> ValidateDocxContainerAsync(byte[] content, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var preambleLength = GetPreambleLength(content);
            await using var source = new MemoryStream(content, preambleLength, content.Length - preambleLength, writable: false);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumDocxEntries)
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không hợp lệ.");

            long decompressedBytes = 0;
            byte[]? contentTypes = null;
            byte[]? documentXml = null;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed > MaximumDocxValidationTime)
                    return new ContainerFailure("UPLOAD_CONTAINER_LIMIT", "DOCX container vượt giới hạn xử lý.");
                if (entry.FullName.Length > 512 || IsUnsafeZipPath(entry.FullName))
                    return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không hợp lệ.");
                if (entry.Length < 0 || entry.Length > MaximumDocxEntryBytes)
                    return new ContainerFailure("UPLOAD_CONTAINER_LIMIT", "DOCX container vượt giới hạn kích thước giải nén.");
                if (entry.FullName.EndsWith('/')) continue;

                var entryResult = await ReadZipEntryAsync(entry, stopwatch, decompressedBytes, cancellationToken);
                decompressedBytes = entryResult.DecompressedBytes;
                if (entry.FullName.Equals("[Content_Types].xml", StringComparison.Ordinal))
                    contentTypes = entryResult.Bytes;
                else if (entry.FullName.Equals("word/document.xml", StringComparison.Ordinal))
                    documentXml = entryResult.Bytes;
            }

            if (contentTypes is null || documentXml is null)
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container thiếu thành phần OpenXML bắt buộc.");
            if (!IsSafeXml(contentTypes) || !IsSafeXml(documentXml))
                return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container chứa XML không hợp lệ.");

            await using var packageStream = new MemoryStream(content, preambleLength, content.Length - preambleLength, writable: false);
            using var document = WordprocessingDocument.Open(packageStream, false);
            return document.MainDocumentPart?.Document?.Body is null
                ? new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không có nội dung tài liệu.")
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không hợp lệ.");
        }
        catch
        {
            return new ContainerFailure("UPLOAD_CONTAINER_INVALID", "DOCX container không hợp lệ.");
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

    private sealed record ContainerFailure(string Code, string Message);
}
