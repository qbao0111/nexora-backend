using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Nexora.Integrations.Storage;

internal interface IR2ObjectClient
{
    Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken);

    Task<long> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken);
}

internal sealed class R2ObjectClient : IR2ObjectClient, IDisposable
{
    private readonly AmazonS3Client client;

    public R2ObjectClient(IOptions<R2StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        var credentials = new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey);
        client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = settings.Endpoint.TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = "auto"
        });
    }

    public async Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key
            }, cancellationToken);
            return new R2ResponseStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("Storage object was not found.", key);
        }
    }

    public async Task<long> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var expectedLength = GetRemainingLength(content);
        var countingContent = expectedLength is null ? new CountingReadStream(content) : null;
        var input = countingContent ?? content;
        try
        {
            await client.PutObjectAsync(CreatePutObjectRequest(bucket, key, input, contentType, expectedLength is null), cancellationToken);
            return expectedLength ?? countingContent!.BytesRead;
        }
        finally
        {
            countingContent?.Dispose();
        }
    }

    public async Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key
            }, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Delete is intentionally idempotent, matching LocalStorageProvider.
        }
    }

    internal static PutObjectRequest CreatePutObjectRequest(
        string bucket,
        string key,
        Stream content,
        string contentType,
        bool useChunkEncoding) => new()
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            UseChunkEncoding = useChunkEncoding
            // CannedACL is deliberately unset: R2 objects remain private by default.
        };

    public void Dispose() => client.Dispose();

    private static long? GetRemainingLength(Stream content) =>
        content.CanSeek ? Math.Max(0, content.Length - content.Position) : null;
}

internal sealed class R2ResponseStream(Stream inner, IDisposable owner) : Stream
{
    private int disposed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            inner.Dispose();
            owner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return base.DisposeAsync();
    }
}

internal sealed class CountingReadStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => Add(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Add(inner.Read(buffer));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        CountAsync(inner.ReadAsync(buffer, offset, count, cancellationToken));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        CountAsync(inner.ReadAsync(buffer, cancellationToken));
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        // The caller owns the original stream.
        base.Dispose(disposing);
    }

    private int Add(int count)
    {
        BytesRead += count;
        return count;
    }

    private async Task<int> CountAsync(Task<int> read)
    {
        return Add(await read);
    }

    private async ValueTask<int> CountAsync(ValueTask<int> read)
    {
        return Add(await read);
    }
}
