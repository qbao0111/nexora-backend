using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class UploadIntentStoreTests
{
    [Fact]
    public async Task IntentStateSurvivesScopesAndCompletionIsReplayable()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
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
            await db.SaveChangesAsync();
        }

        var state = new UploadIntentState(
            Guid.NewGuid(),
            userId,
            $"resumes/{userId:N}/intent.pdf",
            "intent.pdf",
            "application/pdf",
            128,
            now,
            now.AddMinutes(10),
            1,
            null,
            null,
            null);

        using (var createScope = factory.Services.CreateScope())
        {
            var store = createScope.ServiceProvider.GetRequiredService<IUploadIntentStore>();
            var created = await store.CreateAsync(state, "token-hash", CancellationToken.None);
            Assert.Equal(state.Id, created.Id);
        }

        UploadIntentState completed;
        using (var finalizeScope = factory.Services.CreateScope())
        {
            var store = finalizeScope.ServiceProvider.GetRequiredService<IUploadIntentStore>();
            var persisted = await store.FindByTokenHashAsync(userId, "token-hash", CancellationToken.None);
            Assert.NotNull(persisted);

            completed = await store.CompleteAsync(userId, "token-hash", state.ExpectedSize, "checksum", CancellationToken.None)
                ?? throw new InvalidOperationException("The persisted intent was not found.");
        }

        using (var replayScope = factory.Services.CreateScope())
        {
            var store = replayScope.ServiceProvider.GetRequiredService<IUploadIntentStore>();
            var replay = await store.CompleteAsync(userId, "token-hash", state.ExpectedSize, "checksum", CancellationToken.None);

            Assert.NotNull(replay);
            Assert.Equal(completed.CompletedAt, replay!.CompletedAt);
            Assert.Equal(completed.Version, replay.Version);
            Assert.Equal("checksum", replay.Checksum);
        }
    }

    [Fact]
    public async Task ExpiredIntentCannotBeCompleted()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
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
            await db.SaveChangesAsync();
        }

        var state = new UploadIntentState(
            Guid.NewGuid(),
            userId,
            $"resumes/{userId:N}/expired.pdf",
            "expired.pdf",
            "application/pdf",
            128,
            now.AddMinutes(-10),
            now.AddMinutes(-1),
            1,
            null,
            null,
            null);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IUploadIntentStore>();
        await store.CreateAsync(state, "expired-token-hash", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => store.CompleteAsync(
            userId, "expired-token-hash", state.ExpectedSize, "checksum", CancellationToken.None));

        Assert.Equal("UPLOAD_INTENT_INVALID", exception.Code);
    }

    [Fact]
    public async Task CompetingFinalizeScopesConvergeOnOneCompletedState()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        var userId = Guid.NewGuid();
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
            await db.SaveChangesAsync();
        }

        var state = new UploadIntentState(
            Guid.NewGuid(), userId, $"resumes/{userId:N}/concurrent.pdf", "concurrent.pdf",
            "application/pdf", 128, now, now.AddMinutes(10), 1, null, null, null);
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUploadIntentStore>();
            await store.CreateAsync(state, "concurrent-token-hash", CancellationToken.None);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IUploadIntentStore>()
                .CompleteAsync(userId, "concurrent-token-hash", state.ExpectedSize, "checksum", CancellationToken.None);
        }));

        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(results[0]!.CompletedAt, results[1]!.CompletedAt);
        Assert.Equal(results[0]!.Version, results[1]!.Version);
    }
}
