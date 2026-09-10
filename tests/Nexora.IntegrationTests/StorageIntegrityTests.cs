using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class StorageIntegrityTests
{
    [Fact]
    public async Task WorkerRejectsOversizedPostFinalizeObjectWithoutReadingItInFull()
    {
        var expected = "valid"u8.ToArray();
        var tampered = expected.Concat(new byte[100_000]).ToArray();
        var storage = new BoundedReadStorageProvider(tampered);
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>(), services =>
        {
            services.RemoveAll<IStorageProvider>();
            services.AddSingleton<IStorageProvider>(storage);
        });
        factory.InitializeDatabase();
        var userId = Guid.NewGuid();
        var resumeId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = $"{userId:N}@example.test",
                NormalizedUserName = $"{userId:N}@EXAMPLE.TEST",
                Email = $"{userId:N}@example.test",
                NormalizedEmail = $"{userId:N}@EXAMPLE.TEST",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                UserId = userId,
                StorageKey = $"resumes/{userId:N}/cv.pdf",
                FileName = "cv.pdf",
                ContentType = "application/pdf",
                Size = expected.LongLength,
                Checksum = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
                CreatedAt = now
            });
            db.Resumes.Add(new ResumeRecord
            {
                Id = resumeId,
                UserId = userId,
                StoredFileId = storedFileId,
                Status = PracticeValues.Uploaded,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.OutboxEvents.Add(new OutboxEvent
            {
                Id = Guid.NewGuid(),
                Type = "ResumeExtractionRequested",
                AggregateType = "resume",
                AggregateId = resumeId,
                Payload = "{}",
                Status = BillingValues.Pending,
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var processed = await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>()
                .ProcessPendingAsync(CancellationToken.None);
            Assert.Equal(1, processed);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(PracticeValues.Failed, (await db.Resumes.SingleAsync(item => item.Id == resumeId)).Status);
            Assert.Equal(BillingValues.Processed, (await db.OutboxEvents.SingleAsync(item => item.AggregateId == resumeId)).Status);
        }

        Assert.Equal(expected.LongLength + 1, storage.BytesRead);
        Assert.True(storage.BytesRead < tampered.LongLength);
    }

    private sealed class BoundedReadStorageProvider(byte[] content) : IStorageProvider
    {
        public long BytesRead { get; private set; }

        public Task<StoredObject> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredObject("unused", fileName, contentType, 0));

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new ChunkedReadStream(content, count => BytesRead += count));
        }

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ChunkedReadStream(byte[] content, Action<int> onRead) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= content.Length) return 0;
            var length = Math.Min(1, Math.Min(count, content.Length - position));
            content.AsSpan(position, length).CopyTo(buffer.AsSpan(offset, length));
            position += length;
            onRead(length);
            return length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position >= content.Length) return ValueTask.FromResult(0);
            var length = Math.Min(1, Math.Min(buffer.Length, content.Length - position));
            content.AsSpan(position, length).CopyTo(buffer.Span[..length]);
            position += length;
            onRead(length);
            return ValueTask.FromResult(length);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
