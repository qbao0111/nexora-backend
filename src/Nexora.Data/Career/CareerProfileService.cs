using System.Data;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Career;
using Nexora.Business.Common;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Skills;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.Data.Career;

public sealed class CareerProfileService(
    NexoraDbContext dbContext,
    ISkillProfileService skillProfileService,
    TimeProvider timeProvider) : ICareerProfileService
{
    private const int MaximumSummaryItems = 5;

    public async Task<PrimaryResumeSummary?> SetPrimaryResumeAsync(
        Guid userId,
        Guid? resumeId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken);
        await LockUserAsync(userId, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var profile = await dbContext.UserProfiles
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (resumeId is null)
        {
            if (profile is not null)
            {
                profile.PrimaryResumeId = null;
                profile.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var resume = await dbContext.Resumes.AsNoTracking()
            .Where(item => item.Id == resumeId.Value && item.UserId == userId && item.DeletedAt == null)
            .Select(item => new PrimaryResumeRow(item.Id, item.StoredFile.FileName, item.Status, item.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound();
        if (!string.Equals(resume.Status, PracticeValues.Ready, StringComparison.Ordinal))
            throw new BusinessException("RESUME_NOT_READY", "CV chưa sẵn sàng để chọn làm CV chính.", BusinessErrorKind.Conflict);

        if (profile is null)
        {
            profile = new UserProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.UserProfiles.Add(profile);
        }

        profile.PrimaryResumeId = resume.Id;
        profile.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var primaryResume = await LoadPrimaryResumeAsync(resume.Id, userId, cancellationToken)
            ?? throw NotFound();
        await transaction.CommitAsync(cancellationToken);

        return primaryResume;
    }

    public async Task<CareerProfileView> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await dbContext.Users.AsNoTracking()
            .Where(item => item.Id == userId && item.IsActive && item.DeletionRequestedAt == null && item.DeletedAt == null)
            .Select(item => new AccountRow(
                item.Id,
                item.Email,
                item.Profile == null ? null : item.Profile.DisplayName,
                item.Profile == null ? null : item.Profile.YearsOfExperience,
                item.Profile == null ? null : item.Profile.PrimaryResumeId,
                item.Profile == null ? null : item.Profile.AvatarId))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound();

        var primaryResume = await LoadPrimaryResumeAsync(account.PrimaryResumeId, userId, cancellationToken);
        var activeCareerGoal = await dbContext.CareerGoals.AsNoTracking()
            .Where(item => item.UserId == userId && item.Active && item.DeletedAt == null)
            .Select(item => new CareerProfileGoalView(
                item.Id,
                item.TargetRole,
                item.Seniority,
                item.Industry,
                item.TargetCompany,
                item.TargetDate,
                item.Active))
            .SingleOrDefaultAsync(cancellationToken);

        var skillProfile = await skillProfileService.GetAsync(userId, cancellationToken);
        var skillSummary = new CareerProfileSkillSummary(
            skillProfile.Competencies
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.EvidenceCount)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .Take(MaximumSummaryItems)
                .ToArray(),
            skillProfile.WeaknessSignals
                .OrderByDescending(item => item.LatestEvidenceAt)
                .ThenBy(item => item.SourceType, StringComparer.Ordinal)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .Take(MaximumSummaryItems)
                .ToArray());

        var learningPath = activeCareerGoal is null
            ? null
            : await LoadLearningPathSummaryAsync(userId, activeCareerGoal.Id, cancellationToken);
        var hasDisplayName = !string.IsNullOrWhiteSpace(account.DisplayName);
        var hasYearsOfExperience = account.YearsOfExperience is not null;
        var hasPrimaryResume = primaryResume is not null;
        var hasActiveCareerGoal = activeCareerGoal is not null;
        var onboarding = new CareerProfileOnboardingSummary(
            hasDisplayName,
            hasYearsOfExperience,
            hasPrimaryResume,
            hasActiveCareerGoal,
            hasDisplayName && hasYearsOfExperience && hasPrimaryResume && hasActiveCareerGoal);

        return new CareerProfileView(
            new CareerProfileIdentityView(account.Id, account.Email ?? string.Empty, account.DisplayName, account.YearsOfExperience, account.AvatarId),
            primaryResume,
            activeCareerGoal,
            skillSummary,
            learningPath,
            onboarding);
    }

    private async Task<PrimaryResumeSummary?> LoadPrimaryResumeAsync(
        Guid? primaryResumeId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (primaryResumeId is not { } resumeId) return null;

        var resume = await dbContext.Resumes.AsNoTracking()
            .Where(item => item.Id == resumeId && item.UserId == userId && item.DeletedAt == null && item.Status == PracticeValues.Ready)
            .Select(item => new PrimaryResumeRow(item.Id, item.StoredFile.FileName, item.Status, item.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
        if (resume is null) return null;

        var analysisQuery = dbContext.ResumeAnalyses.AsNoTracking()
            .Where(item => item.UserId == userId && item.ResumeId == resume.Id);
        var latestAnalysis = dbContext.Database.IsNpgsql()
            ? await analysisQuery
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Select(item => new ResumeAnalysisSummary(item.Id, item.Mode, item.Status, item.CreatedAt))
                .FirstOrDefaultAsync(cancellationToken)
            : (await analysisQuery
                    .Select(item => new ResumeAnalysisSummary(item.Id, item.Mode, item.Status, item.CreatedAt))
                    .ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefault();

        return new PrimaryResumeSummary(resume.Id, resume.FileName, resume.Status, resume.CreatedAt, latestAnalysis);
    }

    private async Task<CareerProfileLearningPathSummary?> LoadLearningPathSummaryAsync(
        Guid userId,
        Guid careerGoalId,
        CancellationToken cancellationToken)
    {
        var path = await dbContext.LearningPaths.AsNoTracking()
            .Where(item => item.UserId == userId && item.CareerGoalId == careerGoalId)
            .Select(item => new CareerProfileLearningPathSummary(
                item.Id,
                item.Status,
                dbContext.LearningPathActivities.Count(activity => activity.LearningPathId == item.Id && activity.Status == LearningPathValues.Pending),
                dbContext.LearningPathActivities.Count(activity => activity.LearningPathId == item.Id && activity.Status == LearningPathValues.Completed)))
            .SingleOrDefaultAsync(cancellationToken);
        return path;
    }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null) throw NotFound();
    }

    private static BusinessException NotFound() =>
        new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);

    private sealed record AccountRow(Guid Id, string? Email, string? DisplayName, int? YearsOfExperience, Guid? PrimaryResumeId, Guid? AvatarId);
    private sealed record PrimaryResumeRow(Guid Id, string FileName, string Status, DateTimeOffset CreatedAt);
}
