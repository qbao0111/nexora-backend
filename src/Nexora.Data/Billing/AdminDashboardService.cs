using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Admin;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Data.Persistence;

namespace Nexora.Data.Billing;

public sealed class AdminDashboardService(NexoraDbContext dbContext, TimeProvider timeProvider) : IAdminDashboardService
{
    internal const string ReportingTimeZone = "Asia/Ho_Chi_Minh";
    private static readonly string[] AllowedGranularities = ["day", "month", "year"];
    private static readonly string[] AllowedStatuses =
        [BillingValues.Processing, BillingValues.Pending, BillingValues.Fulfilled, BillingValues.Failed];
    private static readonly TimeZoneInfo TimeZone = TimeZoneInfo.FindSystemTimeZoneById(ReportingTimeZone);

    public async Task<AdminDashboardView> GetDashboardAsync(AdminDashboardQuery query, CancellationToken cancellationToken)
    {
        var range = NormalizeRange(query);
        var (fromUtc, toExclusiveUtc) = ToUtcRange(range.From, range.To);
        var now = timeProvider.GetUtcNow();
        var isNpgsql = dbContext.Database.IsNpgsql();

        var totalUsers = await dbContext.Users.AsNoTracking().CountAsync(user => user.DeletedAt == null, cancellationToken);
        var activeUsers = await dbContext.Users.AsNoTracking().CountAsync(user => user.DeletedAt == null && user.IsActive, cancellationToken);
        var deletedUsers = await dbContext.Users.AsNoTracking().CountAsync(user => user.DeletedAt != null, cancellationToken);
        var newUsersInPeriod = isNpgsql
            ? await dbContext.Users.AsNoTracking().CountAsync(
                user => user.DeletedAt == null && user.CreatedAt >= fromUtc && user.CreatedAt < toExclusiveUtc, cancellationToken)
            : (await dbContext.Users.AsNoTracking().Where(user => user.DeletedAt == null)
                .Select(user => user.CreatedAt).ToArrayAsync(cancellationToken))
                .Count(createdAt => createdAt >= fromUtc && createdAt < toExclusiveUtc);

        CurrencySummary[] fulfilledAllTime;
        CurrencySummary[] fulfilledInPeriod;
        if (isNpgsql)
        {
            fulfilledAllTime = await dbContext.Orders.AsNoTracking()
                .Where(order => order.Status == BillingValues.Fulfilled)
                .GroupBy(order => order.Currency)
                .Select(group => new CurrencySummary(group.Key, group.Count(), group.Sum(order => order.AmountMinor)))
                .ToArrayAsync(cancellationToken);
            fulfilledInPeriod = await dbContext.Orders.AsNoTracking()
                .Where(order => order.Status == BillingValues.Fulfilled && order.UpdatedAt >= fromUtc && order.UpdatedAt < toExclusiveUtc)
                .GroupBy(order => order.Currency)
                .Select(group => new CurrencySummary(group.Key, group.Count(), group.Sum(order => order.AmountMinor)))
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            var fulfilledRows = await dbContext.Orders.AsNoTracking().Where(order => order.Status == BillingValues.Fulfilled)
                .Select(order => new RevenueRow(order.UpdatedAt, order.AmountMinor, order.Currency, order.PlanCodeSnapshot))
                .ToArrayAsync(cancellationToken);
            fulfilledAllTime = SummarizeCurrencies(fulfilledRows);
            fulfilledInPeriod = SummarizeCurrencies(fulfilledRows.Where(row => row.UpdatedAt >= fromUtc && row.UpdatedAt < toExclusiveUtc));
        }
        var pendingTransactions = await dbContext.Orders.AsNoTracking()
            .CountAsync(order => order.Status == BillingValues.Pending, cancellationToken);
        var failedTransactions = await dbContext.Orders.AsNoTracking().CountAsync(order => order.Status == BillingValues.Failed, cancellationToken);

        var entitlementQuery = dbContext.Entitlements.AsNoTracking().Where(item => item.Status == BillingValues.Active);
        if (isNpgsql) entitlementQuery = entitlementQuery.Where(item => item.StartsAt <= now && item.EndsAt > now);
        var entitlementRows = await entitlementQuery.Select(item => new { item.UserId, item.PlanCodeSnapshot, item.StartsAt, item.EndsAt })
            .ToArrayAsync(cancellationToken);
        var currentEntitlements = isNpgsql
            ? entitlementRows
            : entitlementRows.Where(item => item.StartsAt <= now && item.EndsAt > now).ToArray();
        var currentPlanByUser = currentEntitlements
            .GroupBy(item => item.UserId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => string.Equals(item.PlanCodeSnapshot, "free", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenByDescending(item => item.EndsAt)
                    .First().PlanCodeSnapshot);
        var nonDeletedUserIds = (await dbContext.Users.AsNoTracking().Where(user => user.DeletedAt == null)
            .Select(user => user.Id).ToArrayAsync(cancellationToken)).ToHashSet();
        var planDistribution = currentPlanByUser
            .Where(item => nonDeletedUserIds.Contains(item.Key))
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AdminPlanDistributionPoint(group.Key, group.Count()))
            .OrderByDescending(item => item.UserCount).ThenBy(item => item.PlanCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var usersWithoutPlan = nonDeletedUserIds.Count(id => !currentPlanByUser.ContainsKey(id));
        if (usersWithoutPlan > 0) planDistribution.Add(new AdminPlanDistributionPoint("none", usersWithoutPlan));
        var activePaidUsers = currentPlanByUser.Count(item => nonDeletedUserIds.Contains(item.Key) &&
            !string.Equals(item.Value, "free", StringComparison.OrdinalIgnoreCase));

        var revenueQuery = dbContext.Orders.AsNoTracking().Where(order => order.Status == BillingValues.Fulfilled && order.Currency == range.Currency);
        var revenueRows = isNpgsql
            ? await revenueQuery.Where(order => order.UpdatedAt >= fromUtc && order.UpdatedAt < toExclusiveUtc)
                .Select(order => new RevenueRow(order.UpdatedAt, order.AmountMinor, order.Currency, order.PlanCodeSnapshot)).ToArrayAsync(cancellationToken)
            : (await revenueQuery.Select(order => new RevenueRow(order.UpdatedAt, order.AmountMinor, order.Currency, order.PlanCodeSnapshot)).ToArrayAsync(cancellationToken))
                .Where(row => row.UpdatedAt >= fromUtc && row.UpdatedAt < toExclusiveUtc).ToArray();
        var userCreatedQuery = dbContext.Users.AsNoTracking().Where(user => user.DeletedAt == null);
        var userCreatedRows = isNpgsql
            ? await userCreatedQuery.Where(user => user.CreatedAt < toExclusiveUtc).Select(user => user.CreatedAt).ToArrayAsync(cancellationToken)
            : (await userCreatedQuery.Select(user => user.CreatedAt).ToArrayAsync(cancellationToken)).Where(createdAt => createdAt < toExclusiveUtc).ToArray();
        var statusQuery = dbContext.Orders.AsNoTracking();
        var orderStatusRows = isNpgsql
            ? await statusQuery.Where(order => order.CreatedAt >= fromUtc && order.CreatedAt < toExclusiveUtc).Select(order => order.Status).ToArrayAsync(cancellationToken)
            : (await statusQuery.Select(order => new { order.CreatedAt, order.Status }).ToArrayAsync(cancellationToken))
                .Where(order => order.CreatedAt >= fromUtc && order.CreatedAt < toExclusiveUtc).Select(order => order.Status).ToArray();

        var buckets = BuildBuckets(range).ToArray();
        var revenueByBucket = revenueRows.GroupBy(row => BucketStart(ToLocalDate(row.UpdatedAt), range.Granularity))
            .ToDictionary(group => group.Key, group => new { Amount = group.Sum(row => row.AmountMinor), Count = group.Count() });
        var newUsersByBucket = userCreatedRows.Where(createdAt => createdAt >= fromUtc)
            .GroupBy(createdAt => BucketStart(ToLocalDate(createdAt), range.Granularity))
            .ToDictionary(group => group.Key, group => group.Count());
        var cumulativeUsers = userCreatedRows.Count(createdAt => createdAt < fromUtc);
        var userGrowth = new List<AdminUserGrowthPoint>(buckets.Length);
        foreach (var bucket in buckets)
        {
            var added = newUsersByBucket.GetValueOrDefault(bucket);
            cumulativeUsers += added;
            userGrowth.Add(new AdminUserGrowthPoint(ToBucketStart(bucket), added, cumulativeUsers));
        }

        var statusDistribution = AllowedStatuses.Select(status =>
            new AdminTransactionStatusPoint(status, orderStatusRows.Count(value => value == status))).ToArray();
        var revenueByPlan = revenueRows.GroupBy(row => row.PlanCodeSnapshot, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AdminPlanRevenuePoint(group.Key, group.Sum(row => row.AmountMinor), group.Count(), range.Currency))
            .OrderByDescending(item => item.AmountMinor).ThenBy(item => item.PlanCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var recentTransactions = isNpgsql
            ? await QueryTransactionRows(dbContext.Orders.AsNoTracking()).Take(8).ToArrayAsync(cancellationToken)
            : (await ProjectTransactionRows(dbContext.Orders.AsNoTracking()).ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(8).ToArray();

        var summary = new AdminDashboardSummary(
            totalUsers, activeUsers, totalUsers - activeUsers, deletedUsers, newUsersInPeriod,
            fulfilledAllTime.Sum(item => item.Count), fulfilledInPeriod.Sum(item => item.Count),
            pendingTransactions, failedTransactions, activePaidUsers,
            fulfilledAllTime.OrderBy(item => item.Currency).Select(item => new AdminCurrencyAmount(item.Currency, item.Amount)).ToArray(),
            fulfilledInPeriod.OrderBy(item => item.Currency).Select(item => new AdminCurrencyAmount(item.Currency, item.Amount)).ToArray());

        return new AdminDashboardView(
            range,
            summary,
            buckets.Select(bucket =>
            {
                var revenue = revenueByBucket.GetValueOrDefault(bucket);
                return new AdminRevenuePoint(
                    ToBucketStart(bucket),
                    BucketLabel(bucket, range.Granularity),
                    revenue?.Amount ?? 0,
                    revenue?.Count ?? 0,
                    range.Currency);
            }).ToArray(),
            userGrowth,
            planDistribution,
            statusDistribution,
            revenueByPlan,
            recentTransactions);
    }

    public async Task<AdminTransactionPage> GetTransactionsAsync(AdminTransactionQuery query, CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 100);
        if (!dbContext.Database.IsNpgsql()) return await GetTransactionsForSqliteAsync(query, pageSize, cancellationToken);
        var orders = dbContext.Orders.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim().ToLowerInvariant();
            if (!AllowedStatuses.Contains(status, StringComparer.Ordinal)) throw Validation("Trạng thái giao dịch không hợp lệ.");
            orders = orders.Where(order => order.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(query.PlanCode))
        {
            var planCode = query.PlanCode.Trim();
            orders = orders.Where(order => EF.Functions.ILike(order.PlanCodeSnapshot, planCode));
        }
        if (!string.IsNullOrWhiteSpace(query.Currency))
        {
            var currency = NormalizeCurrency(query.Currency);
            orders = orders.Where(order => order.Currency == currency);
        }
        if (query.From is not null || query.To is not null)
        {
            var today = ToLocalDate(timeProvider.GetUtcNow());
            var from = query.From ?? query.To ?? today;
            var to = query.To ?? today;
            if (from > to) throw Validation("Khoảng ngày không hợp lệ.");
            var utcRange = ToUtcRange(from, to);
            orders = orders.Where(order => order.CreatedAt >= utcRange.From && order.CreatedAt < utcRange.ToExclusive);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = $"%{EscapeLikePattern(query.Search.Trim())}%";
            orders = orders.Where(order =>
                EF.Functions.ILike(order.User.Email!, search, "\\") ||
                (order.User.Profile != null && order.User.Profile.DisplayName != null && EF.Functions.ILike(order.User.Profile.DisplayName, search, "\\")) ||
                EF.Functions.ILike(order.ProviderTransactionId, search, "\\") ||
                EF.Functions.ILike(order.Id.ToString(), search, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            var cursor = DecodeCursor(query.Cursor);
            orders = orders.Where(order => order.CreatedAt < cursor.CreatedAt ||
                                           (order.CreatedAt == cursor.CreatedAt && order.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await QueryTransactionRows(orders).Take(pageSize + 1).ToArrayAsync(cancellationToken);
        var items = rows.Take(pageSize).ToArray();
        var nextCursor = rows.Length > pageSize && items.Length > 0
            ? EncodeCursor(items[^1].CreatedAt, items[^1].Id)
            : null;
        return new AdminTransactionPage(items, nextCursor, pageSize);
    }

    private AdminDashboardRange NormalizeRange(AdminDashboardQuery query)
    {
        var granularity = string.IsNullOrWhiteSpace(query.Granularity) ? "day" : query.Granularity.Trim().ToLowerInvariant();
        if (!AllowedGranularities.Contains(granularity, StringComparer.Ordinal)) throw Validation("Granularity phải là day, month hoặc year.");
        var today = ToLocalDate(timeProvider.GetUtcNow());
        var to = query.To ?? today;
        var defaultFrom = granularity switch
        {
            "month" => new DateOnly(to.Year, to.Month, 1).AddMonths(-11),
            "year" => new DateOnly(to.Year, 1, 1).AddYears(-4),
            _ => to.AddDays(-29)
        };
        var from = query.From ?? defaultFrom;
        if (from > to) throw Validation("Khoảng ngày không hợp lệ.");
        if ((to.DayNumber - from.DayNumber) > 3660) throw Validation("Khoảng báo cáo tối đa là 10 năm.");
        return new AdminDashboardRange(granularity, from, to, ReportingTimeZone, NormalizeCurrency(query.Currency));
    }

    private static string NormalizeCurrency(string? value)
    {
        var currency = string.IsNullOrWhiteSpace(value) ? "VND" : value.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')) throw Validation("Currency không hợp lệ.");
        return currency;
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private async Task<AdminTransactionPage> GetTransactionsForSqliteAsync(AdminTransactionQuery query, int pageSize, CancellationToken cancellationToken)
    {
        IEnumerable<AdminTransactionView> rows = await ProjectTransactionRows(dbContext.Orders.AsNoTracking()).ToArrayAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim().ToLowerInvariant();
            if (!AllowedStatuses.Contains(status, StringComparer.Ordinal)) throw Validation("Trạng thái giao dịch không hợp lệ.");
            rows = rows.Where(row => row.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(query.PlanCode)) rows = rows.Where(row => string.Equals(row.PlanCode, query.PlanCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.Currency)) rows = rows.Where(row => row.Currency == NormalizeCurrency(query.Currency));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            rows = rows.Where(row => row.UserEmail.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                     (row.UserDisplayName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                     row.ProviderTransactionId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                     row.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (query.From is not null || query.To is not null)
        {
            var today = ToLocalDate(timeProvider.GetUtcNow());
            var from = query.From ?? query.To ?? today;
            var to = query.To ?? today;
            if (from > to) throw Validation("Khoảng ngày không hợp lệ.");
            var utcRange = ToUtcRange(from, to);
            rows = rows.Where(row => row.CreatedAt >= utcRange.From && row.CreatedAt < utcRange.ToExclusive);
        }
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            var cursor = DecodeCursor(query.Cursor);
            rows = rows.Where(row => row.CreatedAt < cursor.CreatedAt || (row.CreatedAt == cursor.CreatedAt && row.Id.CompareTo(cursor.Id) < 0));
        }
        var pageRows = rows.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id).Take(pageSize + 1).ToArray();
        var items = pageRows.Take(pageSize).ToArray();
        return new AdminTransactionPage(items, pageRows.Length > pageSize && items.Length > 0 ? EncodeCursor(items[^1].CreatedAt, items[^1].Id) : null, pageSize);
    }

    private static IQueryable<AdminTransactionView> QueryTransactionRows(IQueryable<Order> orders) =>
        ProjectTransactionRows(orders).OrderByDescending(order => order.CreatedAt).ThenByDescending(order => order.Id);

    private static IQueryable<AdminTransactionView> ProjectTransactionRows(IQueryable<Order> orders) =>
        orders.Select(order => new AdminTransactionView(
                order.Id,
                order.UserId,
                order.User.Email ?? string.Empty,
                order.User.Profile == null ? null : order.User.Profile.DisplayName,
                order.PlanCodeSnapshot,
                order.AmountMinor,
                order.Currency,
                order.Status,
                order.PaymentProvider,
                order.ProviderTransactionId,
                order.CreatedAt,
                order.UpdatedAt,
                order.Status == BillingValues.Fulfilled ? order.UpdatedAt : null));

    private static CurrencySummary[] SummarizeCurrencies(IEnumerable<RevenueRow> rows) => rows
        .GroupBy(row => row.Currency)
        .Select(group => new CurrencySummary(group.Key, group.Count(), group.Sum(row => row.AmountMinor)))
        .ToArray();

    private static IEnumerable<DateOnly> BuildBuckets(AdminDashboardRange range)
    {
        var current = BucketStart(range.From, range.Granularity);
        var last = BucketStart(range.To, range.Granularity);
        while (current <= last)
        {
            yield return current;
            current = range.Granularity switch
            {
                "year" => current.AddYears(1),
                "month" => current.AddMonths(1),
                _ => current.AddDays(1)
            };
        }
    }

    private static DateOnly BucketStart(DateOnly value, string granularity) => granularity switch
    {
        "year" => new DateOnly(value.Year, 1, 1),
        "month" => new DateOnly(value.Year, value.Month, 1),
        _ => value
    };

    private static DateOnly ToLocalDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZone).DateTime);

    private static DateTimeOffset ToBucketStart(DateOnly value)
    {
        var local = value.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local));
    }

    private static string BucketLabel(DateOnly value, string granularity) => granularity switch
    {
        "year" => value.ToString("yyyy", CultureInfo.InvariantCulture),
        "month" => value.ToString("MM/yyyy", CultureInfo.InvariantCulture),
        _ => value.ToString("dd/MM", CultureInfo.InvariantCulture)
    };

    private static (DateTimeOffset From, DateTimeOffset ToExclusive) ToUtcRange(DateOnly from, DateOnly to)
    {
        var fromLocal = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var toLocal = DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(fromLocal, TimeZone)),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(toLocal, TimeZone)));
    }

    private static string EncodeCursor(DateTimeOffset createdAt, Guid id)
    {
        var value = $"{createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (DateTimeOffset CreatedAt, Guid Id) DecodeCursor(string value)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                !Guid.TryParseExact(parts[1], "N", out var id)) throw new FormatException();
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw Validation("Cursor giao dịch không hợp lệ.");
        }
    }

    private static BusinessException Validation(string message) =>
        new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);

    private sealed record CurrencySummary(string Currency, int Count, long Amount);
    private sealed record RevenueRow(DateTimeOffset UpdatedAt, long AmountMinor, string Currency, string PlanCodeSnapshot);
}
