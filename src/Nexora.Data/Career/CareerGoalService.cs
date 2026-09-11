using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Nexora.Business.Career;
using Nexora.Business.Common;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Career;

public sealed partial class CareerGoalService(
    NexoraDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<CareerGoalService> logger) : ICareerGoalService
{
    private const string DeleteOperation = "career-goal.delete";

    public async Task<CareerGoalView> CreateAsync(
        Guid userId,
        CreateCareerGoalCommand command,
        CancellationToken cancellationToken)
    {
        var targetRole = CareerGoalRules.NormalizeRequired(command.TargetRole, CareerGoalRules.TargetRoleMaxLength, "TargetRole");
        var seniority = CareerGoalRules.NormalizeSeniority(command.Seniority);
        var industry = CareerGoalRules.NormalizeOptional(command.Industry, CareerGoalRules.IndustryMaxLength, "Industry");
        var targetCompany = CareerGoalRules.NormalizeOptional(command.TargetCompany, CareerGoalRules.TargetCompanyMaxLength, "TargetCompany");

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        await ValidateTargetJobDescriptionAsync(userId, command.TargetJobDescriptionId, cancellationToken);

        var now = timeProvider.GetUtcNow();
        await DeactivateOtherGoalsAsync(userId, null, now, cancellationToken);
        var goal = new CareerGoal
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TargetRole = targetRole,
            Seniority = seniority,
            Industry = industry,
            TargetCompany = targetCompany,
            TargetJobDescriptionId = command.TargetJobDescriptionId,
            TargetDate = command.TargetDate,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        try
        {
            dbContext.CareerGoals.Add(goal);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsActiveUniqueViolation(exception))
        {
            throw ActiveConflict();
        }
        return Map(goal);
    }

    public async Task<IReadOnlyCollection<CareerGoalView>> GetManyAsync(Guid userId, CancellationToken cancellationToken)
    {
        var query = dbContext.CareerGoals.AsNoTracking().Where(item => item.UserId == userId && item.DeletedAt == null);
        if (!dbContext.Database.IsNpgsql())
            return (await query.ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.Active)
                .ThenByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Select(Map)
                .ToArray();

        return await query
            .OrderByDescending(item => item.Active)
            .ThenByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(MapExpression)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<CareerGoalView> GetAsync(Guid userId, Guid careerGoalId, CancellationToken cancellationToken) =>
        await dbContext.CareerGoals.AsNoTracking()
            .Where(item => item.Id == careerGoalId && item.UserId == userId && item.DeletedAt == null)
            .Select(MapExpression)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw NotFound();

    public async Task<CareerGoalView> UpdateAsync(
        Guid userId,
        Guid careerGoalId,
        UpdateCareerGoalCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.TargetRoleSpecified && !command.SenioritySpecified && !command.IndustrySpecified &&
            !command.TargetCompanySpecified && !command.TargetJobDescriptionIdSpecified &&
            !command.TargetDateSpecified && !command.ActiveSpecified)
            throw Validation("PATCH cần ít nhất một trường để cập nhật.");

        var targetRole = command.TargetRoleSpecified
            ? CareerGoalRules.NormalizeRequired(command.TargetRole, CareerGoalRules.TargetRoleMaxLength, "TargetRole")
            : null;
        var seniority = command.SenioritySpecified ? CareerGoalRules.NormalizeSeniority(command.Seniority) : null;
        var industry = command.IndustrySpecified
            ? CareerGoalRules.NormalizeOptional(command.Industry, CareerGoalRules.IndustryMaxLength, "Industry")
            : null;
        var targetCompany = command.TargetCompanySpecified
            ? CareerGoalRules.NormalizeOptional(command.TargetCompany, CareerGoalRules.TargetCompanyMaxLength, "TargetCompany")
            : null;
        if (command.ActiveSpecified && command.Active is null)
            throw Validation("Active không hợp lệ.");

        var timer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var transactionBeginMs = stageTimer.Elapsed.TotalMilliseconds;

        stageTimer.Restart();
        await LockUserAsync(userId, cancellationToken);
        var userLockMs = stageTimer.Elapsed.TotalMilliseconds;

        stageTimer.Restart();
        var goal = await dbContext.CareerGoals.SingleOrDefaultAsync(
            item => item.Id == careerGoalId && item.UserId == userId && item.DeletedAt == null, cancellationToken)
            ?? throw NotFound();
        var goalLoadMs = stageTimer.Elapsed.TotalMilliseconds;

        if (command.TargetJobDescriptionIdSpecified)
        {
            await ValidateTargetJobDescriptionAsync(userId, command.TargetJobDescriptionId, cancellationToken);
            goal.TargetJobDescriptionId = command.TargetJobDescriptionId;
        }
        if (command.TargetRoleSpecified) goal.TargetRole = targetRole!;
        if (command.SenioritySpecified) goal.Seniority = seniority!;
        if (command.IndustrySpecified) goal.Industry = industry;
        if (command.TargetCompanySpecified) goal.TargetCompany = targetCompany;
        if (command.TargetDateSpecified) goal.TargetDate = command.TargetDate;

        var now = timeProvider.GetUtcNow();
        var activating = command.ActiveSpecified && command.Active == true && !goal.Active;
        var deactivateMs = 0d;
        if (activating)
        {
            stageTimer.Restart();
            await DeactivateOtherGoalsAsync(userId, goal.Id, now, cancellationToken);
            deactivateMs = stageTimer.Elapsed.TotalMilliseconds;
        }
        else if (command.ActiveSpecified && command.Active == false)
        {
            goal.Active = false;
        }
        goal.UpdatedAt = now;

        try
        {
            // Deactivation is already persisted in the transaction before the partial unique index sees this activation.
            if (activating) goal.Active = true;
            stageTimer.Restart();
            await dbContext.SaveChangesAsync(cancellationToken);
            var saveChangesMs = stageTimer.Elapsed.TotalMilliseconds;
            var commitMs = 0d;
            stageTimer.Restart();
            await transaction.CommitAsync(cancellationToken);
            commitMs = stageTimer.Elapsed.TotalMilliseconds;
            CareerGoalMutationTiming(logger, "update", activating, timer.Elapsed.TotalMilliseconds, transactionBeginMs,
                userLockMs, goalLoadMs, deactivateMs, saveChangesMs, commitMs);
        }
        catch (DbUpdateException exception) when (IsActiveUniqueViolation(exception))
        {
            throw ActiveConflict();
        }
        return Map(goal);
    }

    public async Task DeleteAsync(
        Guid userId,
        Guid careerGoalId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = careerGoalId.ToString("N");
        var prior = await FindDeletionAsync(userId, key, cancellationToken);
        if (prior is not null)
        {
            EnsureFingerprint(prior, fingerprint);
            return;
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindDeletionAsync(userId, key, cancellationToken);
        if (prior is not null)
        {
            EnsureFingerprint(prior, fingerprint);
            return;
        }

        await LockUserAsync(userId, cancellationToken);
        var goal = await dbContext.CareerGoals.SingleOrDefaultAsync(
            item => item.Id == careerGoalId && item.UserId == userId && item.DeletedAt == null,
            cancellationToken)
            ?? throw NotFound();

        var now = timeProvider.GetUtcNow();
        goal.Active = false;
        goal.DeletedAt = now;
        goal.UpdatedAt = now;
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Operation = DeleteOperation,
            Key = key,
            RequestFingerprint = fingerprint,
            ResourceId = careerGoalId,
            CreatedAt = now
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsIdempotencyUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            prior = await FindDeletionAsync(userId, key, cancellationToken);
            if (prior is not null)
            {
                EnsureFingerprint(prior, fingerprint);
                return;
            }
            throw;
        }
    }

    private async Task ValidateTargetJobDescriptionAsync(
        Guid userId,
        Guid? targetJobDescriptionId,
        CancellationToken cancellationToken)
    {
        if (!targetJobDescriptionId.HasValue) return;
        if (targetJobDescriptionId.Value == Guid.Empty || !await dbContext.JobDescriptions.AnyAsync(
                item => item.Id == targetJobDescriptionId.Value && item.UserId == userId, cancellationToken))
            throw new BusinessException("JOB_DESCRIPTION_NOT_FOUND", "Không tìm thấy job description.", BusinessErrorKind.NotFound);
    }

    private async Task DeactivateOtherGoalsAsync(
        Guid userId,
        Guid? exceptGoalId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The caller locks the owner row first, so this set-based update preserves the one-active-goal transition
        // while avoiding a read of every active goal and a separate tracked SaveChanges round-trip.
        await dbContext.CareerGoals
            .Where(item => item.UserId == userId && item.Active && item.DeletedAt == null &&
                (!exceptGoalId.HasValue || item.Id != exceptGoalId.Value))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Active, false)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null) throw NotFound();
    }

    private Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.BeginTransactionAsync(
            dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken);

    private Task<IdempotencyRecord?> FindDeletionAsync(Guid userId, string key, CancellationToken cancellationToken) =>
        dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == DeleteOperation && item.Key == key,
            cancellationToken);

    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
        ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
        : value.Trim();

    private static void EnsureFingerprint(IdempotencyRecord prior, string fingerprint)
    {
        if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
    }

    private static CareerGoalView Map(CareerGoal goal) =>
        new(goal.Id, goal.TargetRole, goal.Seniority, goal.Industry, goal.TargetCompany,
            goal.TargetJobDescriptionId, goal.TargetDate, goal.Active, goal.CreatedAt, goal.UpdatedAt);

    private static readonly System.Linq.Expressions.Expression<Func<CareerGoal, CareerGoalView>> MapExpression =
        item => new CareerGoalView(item.Id, item.TargetRole, item.Seniority, item.Industry, item.TargetCompany,
            item.TargetJobDescriptionId, item.TargetDate, item.Active, item.CreatedAt, item.UpdatedAt);

    private static BusinessException Validation(string message) =>
        new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);

    private static BusinessException NotFound() =>
        new("CAREER_GOAL_NOT_FOUND", "Không tìm thấy career goal.", BusinessErrorKind.NotFound);

    private static BusinessException ActiveConflict() =>
        new("CAREER_GOAL_ACTIVE_CONFLICT", "Career goal khác vừa được kích hoạt. Vui lòng tải lại danh sách và thử lại.", BusinessErrorKind.Conflict);

    private static bool IsActiveUniqueViolation(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("IX_career_goals_one_active_per_user", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("career_goals.UserId", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdempotencyUniqueViolation(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("idempotency_keys", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("IdempotencyRecords", StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(
        EventId = 9101,
        Level = LogLevel.Information,
        Message = "Career goal mutation timing {Operation} {ActiveTransition} {TotalMs} {TransactionBeginMs} {UserLockMs} {GoalLoadMs} {DeactivateMs} {SaveChangesMs} {CommitMs}")]
    private static partial void CareerGoalMutationTiming(
        ILogger logger,
        string operation,
        bool activeTransition,
        double totalMs,
        double transactionBeginMs,
        double userLockMs,
        double goalLoadMs,
        double deactivateMs,
        double saveChangesMs,
        double commitMs);
}
