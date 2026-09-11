using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Business.Common;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Skills;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

namespace Nexora.Data.Learning;

public sealed class LearningPathService(
    NexoraDbContext dbContext,
    ISkillProfileService skillProfileService,
    TimeProvider timeProvider) : ILearningPathService
{
    public async Task<LearningPathView> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var careerGoal = await GetActiveCareerGoalAsync(userId, cancellationToken);
        var path = await LoadPathByGoalAsync(userId, careerGoal.Id, tracking: false, cancellationToken)
            ?? throw PathNotFound();
        return Map(path);
    }

    public async Task<LearningPathProvisionResult> GenerateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var activeGoal = await GetActiveCareerGoalAsync(userId, cancellationToken);
        var existing = await LoadPathByGoalAsync(userId, activeGoal.Id, tracking: false, cancellationToken);
        if (existing is not null)
            return new LearningPathProvisionResult(Map(existing), false);

        var plan = await BuildPlanAsync(userId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        activeGoal = await GetActiveCareerGoalAsync(userId, cancellationToken);
        existing = await LoadPathByGoalAsync(userId, activeGoal.Id, tracking: true, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new LearningPathProvisionResult(Map(existing), false);
        }

        var path = CreatePath(userId, activeGoal.Id, plan, timeProvider.GetUtcNow());
        dbContext.LearningPaths.Add(path);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LearningPathProvisionResult(Map(path), true);
        }
        catch (DbUpdateException exception) when (IsPathUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return new LearningPathProvisionResult(await GetAsync(userId, cancellationToken), false);
        }
    }

    public async Task<LearningPathView> RefreshAsync(Guid userId, CancellationToken cancellationToken)
    {
        _ = await GetActiveCareerGoalAsync(userId, cancellationToken);
        var plan = await BuildPlanAsync(userId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        var activeGoal = await GetActiveCareerGoalAsync(userId, cancellationToken);
        var path = await LoadPathByGoalAsync(userId, activeGoal.Id, tracking: true, cancellationToken);
        if (path is null)
        {
            path = CreatePath(userId, activeGoal.Id, plan, timeProvider.GetUtcNow());
            dbContext.LearningPaths.Add(path);
        }
        else
        {
            Reconcile(path, plan, timeProvider.GetUtcNow());
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Map(path);
        }
        catch (DbUpdateException exception) when (IsPathUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await GetAsync(userId, cancellationToken);
        }
    }

    public async Task<LearningPathView> UpdateActivityAsync(
        Guid userId,
        Guid activityId,
        UpdateLearningPathActivityCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(command.Status?.Trim(), LearningPathValues.Completed, StringComparison.Ordinal))
            throw new BusinessException("LEARNING_PATH_ACTIVITY_STATUS_INVALID", "Chỉ có thể đánh dấu hoạt động là completed.", BusinessErrorKind.Validation);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        var activity = await dbContext.LearningPathActivities
            .Where(item => item.Id == activityId && item.LearningPath.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw ActivityNotFound();

        if (activity.Status == LearningPathValues.Obsolete)
            throw new BusinessException("LEARNING_PATH_ACTIVITY_OBSOLETE", "Hoạt động này không còn nằm trong learning path hiện tại.", BusinessErrorKind.Conflict);
        if (activity.Status is not LearningPathValues.Pending and not LearningPathValues.Completed)
            throw new BusinessException("LEARNING_PATH_ACTIVITY_INVALID_STATE", "Hoạt động không ở trạng thái hợp lệ.", BusinessErrorKind.Conflict);

        var now = timeProvider.GetUtcNow();
        if (activity.Status == LearningPathValues.Pending)
        {
            activity.Status = LearningPathValues.Completed;
            activity.CompletedAt = now;
            activity.UpdatedAt = now;
        }

        var path = await LoadPathByIdAsync(userId, activity.LearningPathId, tracking: true, cancellationToken)
            ?? throw PathNotFound();
        UpdateMilestoneStatuses(path, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(path);
    }

    private async Task<LearningPathPlan> BuildPlanAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await skillProfileService.GetAsync(userId, cancellationToken);
        var resources = await dbContext.Scenarios.AsNoTracking()
            .Where(item => item.Status == PracticeFeatureValues.Published)
            .Select(item => new LearningPathScenarioResource(item.Id, item.Competency))
            .ToArrayAsync(cancellationToken);
        return LearningPathPlanner.Create(profile, resources);
    }

    private async Task<CareerGoal> GetActiveCareerGoalAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.CareerGoals.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.Active && item.DeletedAt == null, cancellationToken)
            ?? throw ActiveCareerGoalRequired();
    }

    private async Task<LearningPath?> LoadPathByGoalAsync(
        Guid userId,
        Guid careerGoalId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = dbContext.LearningPaths
            .Include(item => item.Milestones)
            .ThenInclude(item => item.Activities)
            .Where(item => item.UserId == userId && item.CareerGoalId == careerGoalId);
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<LearningPath?> LoadPathByIdAsync(
        Guid userId,
        Guid pathId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = dbContext.LearningPaths
            .Include(item => item.Milestones)
            .ThenInclude(item => item.Activities)
            .Where(item => item.Id == pathId && item.UserId == userId);
        if (!tracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(cancellationToken);
    }

    private static LearningPath CreatePath(
        Guid userId,
        Guid careerGoalId,
        LearningPathPlan plan,
        DateTimeOffset now)
    {
        var path = new LearningPath
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CareerGoalId = careerGoalId,
            Status = LearningPathValues.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        var milestones = plan.Milestones.ToDictionary(
            item => item.Code,
            item => new LearningPathMilestone
            {
                Id = Guid.NewGuid(),
                LearningPathId = path.Id,
                Code = item.Code,
                Title = item.Title,
                SortOrder = item.SortOrder,
                Status = LearningPathValues.Active,
                CreatedAt = now,
                UpdatedAt = now
            },
            StringComparer.Ordinal);

        foreach (var milestone in milestones.Values)
            path.Milestones.Add(milestone);

        foreach (var item in plan.Activities)
        {
            var milestone = milestones[item.MilestoneCode];
            milestone.Activities.Add(new LearningPathActivity
            {
                Id = Guid.NewGuid(),
                LearningPathId = path.Id,
                LearningPathMilestoneId = milestone.Id,
                ActivityKey = item.Key,
                Type = item.Type,
                Title = item.Title,
                Description = item.Description,
                CompetencyCode = item.CompetencyCode,
                ResourceId = item.ResourceId,
                ExternalUrl = item.ExternalUrl,
                Priority = item.Priority,
                SortOrder = item.SortOrder,
                Status = LearningPathValues.Pending,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        UpdateMilestoneStatuses(path, now);
        return path;
    }

    private void Reconcile(LearningPath path, LearningPathPlan plan, DateTimeOffset now)
    {
        var changed = false;
        var milestonesByCode = path.Milestones.ToDictionary(item => item.Code, StringComparer.Ordinal);
        foreach (var planMilestone in plan.Milestones)
        {
            if (!milestonesByCode.TryGetValue(planMilestone.Code, out var milestone))
            {
                milestone = new LearningPathMilestone
                {
                    Id = Guid.NewGuid(),
                    LearningPathId = path.Id,
                    Code = planMilestone.Code,
                    Title = planMilestone.Title,
                    SortOrder = planMilestone.SortOrder,
                    Status = LearningPathValues.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                path.Milestones.Add(milestone);
                dbContext.LearningPathMilestones.Add(milestone);
                milestonesByCode.Add(milestone.Code, milestone);
                changed = true;
            }

            changed |= UpdateMilestoneMetadata(milestone, planMilestone, now);
        }

        var existingByKey = path.Milestones
            .SelectMany(item => item.Activities)
            .GroupBy(item => item.ActivityKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Id).First(), StringComparer.Ordinal);
        var desiredKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var planActivity in plan.Activities)
        {
            var effectivePlan = ResolveCurrentActivityPlan(planActivity, existingByKey);
            desiredKeys.Add(effectivePlan.Key);
            var milestone = milestonesByCode[effectivePlan.MilestoneCode];
            if (!existingByKey.TryGetValue(effectivePlan.Key, out var activity))
            {
                var newActivity = CreateActivity(path, milestone, effectivePlan, now);
                milestone.Activities.Add(newActivity);
                dbContext.LearningPathActivities.Add(newActivity);
                existingByKey.Add(effectivePlan.Key, newActivity);
                changed = true;
                continue;
            }

            if (activity.LearningPathMilestoneId != milestone.Id)
            {
                activity.Milestone.Activities.Remove(activity);
                milestone.Activities.Add(activity);
                activity.LearningPathMilestoneId = milestone.Id;
                activity.Milestone = milestone;
                activity.UpdatedAt = now;
                changed = true;
            }

            if (activity.Status == LearningPathValues.Obsolete)
            {
                activity.Status = LearningPathValues.Pending;
                activity.CompletedAt = null;
                activity.UpdatedAt = now;
                changed = true;
            }

            changed |= UpdateActivityMetadata(activity, effectivePlan, now);
        }

        foreach (var activity in path.Milestones.SelectMany(item => item.Activities))
        {
            if (!desiredKeys.Contains(activity.ActivityKey) && activity.Status == LearningPathValues.Pending)
            {
                activity.Status = LearningPathValues.Obsolete;
                activity.CompletedAt = null;
                activity.UpdatedAt = now;
                changed = true;
            }
        }

        changed |= UpdateMilestoneStatuses(path, now);
        if (changed) path.UpdatedAt = now;
    }

    private static LearningPathActivityPlan ResolveCurrentActivityPlan(
        LearningPathActivityPlan plan,
        IReadOnlyDictionary<string, LearningPathActivity> existingByKey)
    {
        if (!existingByKey.TryGetValue(plan.Key, out var original) ||
            original.Status != LearningPathValues.Completed ||
            original.CompletedAt is not { } completedAt ||
            plan.LatestEvidenceAt is not { } latestEvidenceAt ||
            latestEvidenceAt <= completedAt)
            return plan;

        var cyclePrefix = $"{plan.Key}:cycle:";
        var pendingCycle = existingByKey.Values
            .Where(item => item.Status == LearningPathValues.Pending &&
                           item.ActivityKey.StartsWith(cyclePrefix, StringComparison.Ordinal))
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        return plan with
        {
            Key = pendingCycle?.ActivityKey ??
                  LearningPathRules.LearningCycleActivityKey(plan.Key, latestEvidenceAt)
        };
    }

    private static LearningPathActivity CreateActivity(
        LearningPath path,
        LearningPathMilestone milestone,
        LearningPathActivityPlan plan,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            LearningPathId = path.Id,
            LearningPathMilestoneId = milestone.Id,
            ActivityKey = plan.Key,
            Type = plan.Type,
            Title = plan.Title,
            Description = plan.Description,
            CompetencyCode = plan.CompetencyCode,
            ResourceId = plan.ResourceId,
            ExternalUrl = plan.ExternalUrl,
            Priority = plan.Priority,
            SortOrder = plan.SortOrder,
            Status = LearningPathValues.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static bool UpdateActivityMetadata(
        LearningPathActivity activity,
        LearningPathActivityPlan plan,
        DateTimeOffset now)
    {
        var changed = false;
        if (activity.Type != plan.Type) { activity.Type = plan.Type; changed = true; }
        if (activity.Title != plan.Title) { activity.Title = plan.Title; changed = true; }
        if (activity.Description != plan.Description) { activity.Description = plan.Description; changed = true; }
        if (activity.CompetencyCode != plan.CompetencyCode) { activity.CompetencyCode = plan.CompetencyCode; changed = true; }
        if (activity.ResourceId != plan.ResourceId) { activity.ResourceId = plan.ResourceId; changed = true; }
        if (activity.ExternalUrl != plan.ExternalUrl) { activity.ExternalUrl = plan.ExternalUrl; changed = true; }
        if (activity.Priority != plan.Priority) { activity.Priority = plan.Priority; changed = true; }
        if (activity.SortOrder != plan.SortOrder) { activity.SortOrder = plan.SortOrder; changed = true; }
        if (changed) activity.UpdatedAt = now;
        return changed;
    }

    private static bool UpdateMilestoneMetadata(
        LearningPathMilestone milestone,
        LearningPathMilestonePlan plan,
        DateTimeOffset now)
    {
        var changed = false;
        if (milestone.Title != plan.Title) { milestone.Title = plan.Title; changed = true; }
        if (milestone.SortOrder != plan.SortOrder) { milestone.SortOrder = plan.SortOrder; changed = true; }
        if (changed) milestone.UpdatedAt = now;
        return changed;
    }

    private static bool UpdateMilestoneStatuses(LearningPath path, DateTimeOffset now)
    {
        var changed = false;
        foreach (var milestone in path.Milestones)
        {
            var current = milestone.Activities.Where(item => item.Status != LearningPathValues.Obsolete).ToArray();
            var status = current.Length == 0
                ? LearningPathValues.Obsolete
                : current.All(item => item.Status == LearningPathValues.Completed)
                    ? LearningPathValues.Completed
                    : LearningPathValues.Active;
            if (milestone.Status != status)
            {
                milestone.Status = status;
                milestone.UpdatedAt = now;
                changed = true;
            }
        }

        return changed;
    }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null) throw ActiveCareerGoalRequired();
    }

    private Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.BeginTransactionAsync(
            dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken);

    private static LearningPathView Map(LearningPath path)
    {
        var activities = path.Milestones.SelectMany(item => item.Activities).ToArray();
        var total = activities.Count(item => item.Status != LearningPathValues.Obsolete);
        var completed = activities.Count(item => item.Status == LearningPathValues.Completed);
        var progress = new LearningPathProgressView(
            completed,
            total,
            total == 0 ? 0 : (int)Math.Round(completed * 100d / total, MidpointRounding.AwayFromZero));
        var milestones = path.Milestones
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Select(item => new LearningPathMilestoneView(
                item.Id,
                item.Code,
                item.Title,
                item.SortOrder,
                item.Status,
                item.Activities
                    .OrderBy(activity => activity.SortOrder)
                    .ThenBy(activity => activity.ActivityKey, StringComparer.Ordinal)
                    .ThenBy(activity => activity.Id)
                    .Select(activity => new LearningPathActivityView(
                        activity.Id,
                        activity.Type,
                        activity.Title,
                        activity.Description,
                        activity.CompetencyCode,
                        activity.ResourceId,
                        activity.ExternalUrl,
                        activity.Priority,
                        activity.Status,
                        activity.SortOrder,
                        activity.CompletedAt,
                        activity.CreatedAt,
                        activity.UpdatedAt))
                    .ToArray()))
            .ToArray();
        return new LearningPathView(path.Id, path.CareerGoalId, path.Status, path.CreatedAt, path.UpdatedAt, progress, milestones);
    }

    private static BusinessException ActiveCareerGoalRequired() =>
        new("ACTIVE_CAREER_GOAL_REQUIRED", "Hãy tạo hoặc kích hoạt career goal trước khi tạo learning path.", BusinessErrorKind.Validation);

    private static BusinessException PathNotFound() =>
        new("LEARNING_PATH_NOT_FOUND", "Không tìm thấy learning path.", BusinessErrorKind.NotFound);

    private static BusinessException ActivityNotFound() =>
        new("LEARNING_PATH_ACTIVITY_NOT_FOUND", "Không tìm thấy hoạt động trong learning path.", BusinessErrorKind.NotFound);

    private static bool IsPathUniqueViolation(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("IX_learning_paths_one_per_user_career_goal", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("learning_paths.UserId", StringComparison.OrdinalIgnoreCase);
    }
}
