using Microsoft.EntityFrameworkCore;
using Nexora.Business.Practice;
using Nexora.Business.Skills;
using Nexora.Data.Career;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class PrimaryResumePostgresTests
{
    [PostgresFact]
    public async Task SetAndReadPrimaryResumeUsesServerSideLatestAnalysisOrdering()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var options = new DbContextOptionsBuilder<NexoraDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new NexoraDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var zeroAnalysisResumeId = Guid.NewGuid();
        var selectedResumeId = Guid.NewGuid();
        var otherResumeId = Guid.NewGuid();
        var foreignResumeId = Guid.NewGuid();
        var tieTime = new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
        var expectedAnalysisId = Guid.Parse("30000000-0000-0000-0000-000000000002");

        db.Users.AddRange(NewUser(userId, "owner"), NewUser(otherUserId, "other"));
        AddResume(db, userId, zeroAnalysisResumeId, "zero-analysis.pdf", tieTime.AddDays(-4));
        AddResume(db, userId, selectedResumeId, "selected.pdf", tieTime.AddDays(-3));
        AddResume(db, userId, otherResumeId, "other-resume.pdf", tieTime.AddDays(-2));
        AddResume(db, otherUserId, foreignResumeId, "foreign.pdf", tieTime.AddDays(-1));
        await db.SaveChangesAsync();

        var service = new CareerProfileService(db, new EmptySkillProfileService(), TimeProvider.System);
        var zeroAnalysisSelection = await service.SetPrimaryResumeAsync(userId, zeroAnalysisResumeId, CancellationToken.None);
        Assert.NotNull(zeroAnalysisSelection);
        Assert.Null(zeroAnalysisSelection.LatestAnalysis);
        Assert.Null((await service.GetAsync(userId, CancellationToken.None)).PrimaryResume?.LatestAnalysis);

        db.ResumeAnalyses.AddRange(
            NewAnalysis(userId, selectedResumeId, Guid.Parse("10000000-0000-0000-0000-000000000001"), tieTime.AddMinutes(-1)),
            NewAnalysis(userId, selectedResumeId, Guid.Parse("30000000-0000-0000-0000-000000000001"), tieTime),
            NewAnalysis(userId, selectedResumeId, expectedAnalysisId, tieTime),
            NewAnalysis(userId, otherResumeId, Guid.NewGuid(), tieTime.AddDays(1)),
            NewAnalysis(otherUserId, foreignResumeId, Guid.NewGuid(), tieTime.AddDays(2)));
        await db.SaveChangesAsync();

        var selected = await service.SetPrimaryResumeAsync(userId, selectedResumeId, CancellationToken.None);
        Assert.Equal(expectedAnalysisId, selected?.LatestAnalysis?.Id);

        var profile = await service.GetAsync(userId, CancellationToken.None);
        Assert.Equal(selectedResumeId, profile.PrimaryResume?.Id);
        Assert.Equal(expectedAnalysisId, profile.PrimaryResume?.LatestAnalysis?.Id);
    }

    private static ApplicationUser NewUser(Guid id, string name) => new()
    {
        Id = id,
        UserName = $"{name}@example.test",
        NormalizedUserName = $"{name.ToUpperInvariant()}@EXAMPLE.TEST",
        Email = $"{name}@example.test",
        NormalizedEmail = $"{name.ToUpperInvariant()}@EXAMPLE.TEST",
        EmailConfirmed = true,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        SecurityStamp = Guid.NewGuid().ToString("N")
    };

    private static void AddResume(
        NexoraDbContext db,
        Guid userId,
        Guid resumeId,
        string fileName,
        DateTimeOffset createdAt)
    {
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = $"postgres-test/{resumeId:N}",
            FileName = fileName,
            ContentType = "application/pdf",
            Size = 128,
            Checksum = Guid.NewGuid().ToString("N"),
            CreatedAt = createdAt
        };
        db.AddRange(file, new ResumeRecord
        {
            Id = resumeId,
            UserId = userId,
            StoredFileId = file.Id,
            Status = PracticeValues.Ready,
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        });
    }

    private static ResumeAnalysis NewAnalysis(
        Guid userId,
        Guid resumeId,
        Guid analysisId,
        DateTimeOffset createdAt) => new()
        {
            Id = analysisId,
            UserId = userId,
            ResumeId = resumeId,
            ResumeVersion = 1,
            Mode = ResumeAnalysisModes.FieldBenchmark,
            Status = PracticeValues.Completed,
            ModelVersion = "postgres-test-model",
            PromptVersion = "postgres-test-prompt",
            SchemaVersion = "postgres-test-schema",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            CompletedAt = createdAt
        };

    private sealed class EmptySkillProfileService : ISkillProfileService
    {
        public Task<SkillProfileView> GetAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(new SkillProfileView([], []));
    }
}

[CollectionDefinition("PostgreSQL primary resume", DisableParallelization = true)]
public sealed class PrimaryResumePostgresGroup;

public sealed class PostgresFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "NEXORA_POSTGRES_TEST_CONNECTION";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            Skip = $"Set {ConnectionVariable} to run the PostgreSQL regression.";
    }
}
