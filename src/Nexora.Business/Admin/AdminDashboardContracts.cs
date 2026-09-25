namespace Nexora.Business.Admin;

public sealed record AdminDashboardQuery(string? Granularity, DateOnly? From, DateOnly? To, string? Currency);

public sealed record AdminDashboardRange(
    string Granularity,
    DateOnly From,
    DateOnly To,
    string TimeZone,
    string Currency);

public sealed record AdminCurrencyAmount(string Currency, long AmountMinor);

public sealed record AdminDashboardSummary(
    int TotalUsers,
    int ActiveUsers,
    int InactiveUsers,
    int DeletedUsers,
    int NewUsersInPeriod,
    int FulfilledTransactionsAllTime,
    int FulfilledTransactionsInPeriod,
    int PendingTransactions,
    int FailedTransactions,
    int ActivePaidUsers,
    IReadOnlyCollection<AdminCurrencyAmount> TotalRevenueByCurrency,
    IReadOnlyCollection<AdminCurrencyAmount> PeriodRevenueByCurrency);

public sealed record AdminRevenuePoint(
    DateTimeOffset BucketStart,
    string Label,
    long AmountMinor,
    int TransactionCount,
    string Currency);

public sealed record AdminUserGrowthPoint(DateTimeOffset BucketStart, int NewUsers, int CumulativeUsers);
public sealed record AdminPlanDistributionPoint(string PlanCode, int UserCount);
public sealed record AdminTransactionStatusPoint(string Status, int Count);
public sealed record AdminPlanRevenuePoint(string PlanCode, long AmountMinor, int TransactionCount, string Currency);

public sealed record AdminTransactionView(
    Guid Id,
    Guid UserId,
    string UserEmail,
    string? UserDisplayName,
    string PlanCode,
    long AmountMinor,
    string Currency,
    string Status,
    string PaymentProvider,
    string ProviderTransactionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FulfilledAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record AdminDashboardView(
    AdminDashboardRange Range,
    AdminDashboardSummary Summary,
    IReadOnlyCollection<AdminRevenuePoint> RevenueSeries,
    IReadOnlyCollection<AdminUserGrowthPoint> UserGrowthSeries,
    IReadOnlyCollection<AdminPlanDistributionPoint> PlanDistribution,
    IReadOnlyCollection<AdminTransactionStatusPoint> TransactionStatusDistribution,
    IReadOnlyCollection<AdminPlanRevenuePoint> RevenueByPlan,
    IReadOnlyCollection<AdminTransactionView> RecentTransactions);

public sealed record AdminTransactionQuery(
    string? Search,
    string? Status,
    string? PlanCode,
    string? Currency,
    DateOnly? From,
    DateOnly? To,
    string? Cursor,
    int PageSize);

public sealed record AdminTransactionPage(
    IReadOnlyCollection<AdminTransactionView> Items,
    string? NextCursor,
    int PageSize);

public interface IAdminDashboardService
{
    Task<AdminDashboardView> GetDashboardAsync(AdminDashboardQuery query, CancellationToken cancellationToken);
    Task<AdminTransactionPage> GetTransactionsAsync(AdminTransactionQuery query, CancellationToken cancellationToken);
}
