using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nexora.Data.Billing;
using Nexora.Data.Identity;

namespace Nexora.Data.Persistence;

public sealed class NexoraDbContext(DbContextOptions<NexoraDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanPrice> PlanPrices => Set<PlanPrice>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ApplicationUser>().ToTable("asp_net_users");
        builder.Entity<IdentityRole<Guid>>().ToTable("asp_net_roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("asp_net_user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("asp_net_user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("asp_net_user_logins");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("asp_net_role_claims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("asp_net_user_tokens");

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.CreatedAt).IsRequired();
            entity.Property(user => user.UpdatedAt).IsRequired();
        });
        builder.Entity<UserProfile>(entity =>
        {
            entity.ToTable("user_profiles");
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => profile.UserId).IsUnique();
            entity.Property(profile => profile.DisplayName).HasMaxLength(120);
            entity.HasOne(profile => profile.User).WithOne(user => user.Profile)
                .HasForeignKey<UserProfile>(profile => profile.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.ExpiresAt });
            entity.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(token => token.ReplacedByTokenHash).HasMaxLength(64);
            entity.Property(token => token.ConcurrencyToken).IsConcurrencyToken();
            entity.HasOne(token => token.User).WithMany(user => user.RefreshTokens)
                .HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        ConfigureBilling(builder);
    }

    private static void ConfigureBilling(ModelBuilder builder)
    {
        builder.Entity<Plan>(entity =>
        {
            entity.ToTable("plans");
            entity.HasKey(plan => plan.Id);
            entity.HasIndex(plan => plan.Code).IsUnique();
            entity.Property(plan => plan.Code).HasMaxLength(40).IsRequired();
            entity.Property(plan => plan.Name).HasMaxLength(120).IsRequired();
            var createdAt = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
            entity.HasData(
                new Plan { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Code = "free", Name = "Free", SortOrder = 0, IsActive = true, CreatedAt = createdAt },
                new Plan { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Code = "basic", Name = "Basic", SortOrder = 1, IsActive = true, CreatedAt = createdAt },
                new Plan { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Code = "weekly", Name = "Weekly", SortOrder = 2, IsActive = true, CreatedAt = createdAt },
                new Plan { Id = Guid.Parse("00000000-0000-0000-0000-000000000004"), Code = "pro", Name = "Pro", SortOrder = 3, IsActive = true, CreatedAt = createdAt });
        });
        builder.Entity<PlanPrice>(entity =>
        {
            entity.ToTable("plan_prices");
            entity.HasKey(price => price.Id);
            entity.HasIndex(price => new { price.PlanId, price.Currency, price.CreatedAt }).IsUnique();
            entity.Property(price => price.Currency).HasMaxLength(3).IsRequired();
            entity.HasOne(price => price.Plan).WithMany(plan => plan.Prices)
                .HasForeignKey(price => price.PlanId).OnDelete(DeleteBehavior.Restrict);
            var createdAt = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
            entity.HasData(
                new PlanPrice { Id = Guid.Parse("10000000-0000-0000-0000-000000000001"), PlanId = Guid.Parse("00000000-0000-0000-0000-000000000001"), AmountMinor = 0, Currency = "VND", DurationDays = null, InterviewQuota = 1, IsActive = true, CreatedAt = createdAt },
                new PlanPrice { Id = Guid.Parse("10000000-0000-0000-0000-000000000002"), PlanId = Guid.Parse("00000000-0000-0000-0000-000000000002"), AmountMinor = 49_000, Currency = "VND", DurationDays = 3, InterviewQuota = 3, IsActive = true, CreatedAt = createdAt },
                new PlanPrice { Id = Guid.Parse("10000000-0000-0000-0000-000000000003"), PlanId = Guid.Parse("00000000-0000-0000-0000-000000000003"), AmountMinor = 189_000, Currency = "VND", DurationDays = 14, InterviewQuota = 20, IsActive = true, CreatedAt = createdAt },
                new PlanPrice { Id = Guid.Parse("10000000-0000-0000-0000-000000000004"), PlanId = Guid.Parse("00000000-0000-0000-0000-000000000004"), AmountMinor = 599_000, Currency = "VND", DurationDays = 90, InterviewQuota = null, IsActive = true, CreatedAt = createdAt });
        });
        builder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(order => order.Id);
            entity.HasIndex(order => new { order.UserId, order.CreatedAt });
            entity.HasIndex(order => new { order.PaymentProvider, order.ProviderTransactionId }).IsUnique();
            entity.Property(order => order.PlanCodeSnapshot).HasMaxLength(40).IsRequired();
            entity.Property(order => order.Currency).HasMaxLength(3).IsRequired();
            entity.Property(order => order.Status).HasMaxLength(20).IsRequired();
            entity.Property(order => order.PaymentProvider).HasMaxLength(40).IsRequired();
            entity.Property(order => order.ProviderTransactionId).HasMaxLength(160).IsRequired();
            entity.Property(order => order.CheckoutUrl).HasMaxLength(2048).IsRequired();
            entity.HasOne(order => order.User).WithMany().HasForeignKey(order => order.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(order => order.PlanPrice).WithMany().HasForeignKey(order => order.PlanPriceId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<PaymentEvent>(entity =>
        {
            entity.ToTable("payment_events");
            entity.HasKey(paymentEvent => paymentEvent.Id);
            entity.HasIndex(paymentEvent => new { paymentEvent.Provider, paymentEvent.ProviderEventId }).IsUnique();
            entity.Property(paymentEvent => paymentEvent.Provider).HasMaxLength(40).IsRequired();
            entity.Property(paymentEvent => paymentEvent.ProviderEventId).HasMaxLength(160).IsRequired();
            entity.HasOne(paymentEvent => paymentEvent.Order).WithMany(order => order.PaymentEvents)
                .HasForeignKey(paymentEvent => paymentEvent.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Subscription>(entity =>
        {
            entity.ToTable("subscriptions");
            entity.HasKey(subscription => subscription.Id);
            entity.HasIndex(subscription => subscription.OrderId).IsUnique();
            entity.HasIndex(subscription => new { subscription.UserId, subscription.EndsAt });
            entity.Property(subscription => subscription.Status).HasMaxLength(20).IsRequired();
            entity.HasOne(subscription => subscription.User).WithMany().HasForeignKey(subscription => subscription.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(subscription => subscription.Order).WithOne().HasForeignKey<Subscription>(subscription => subscription.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Entitlement>(entity =>
        {
            entity.ToTable("entitlements");
            entity.HasKey(entitlement => entitlement.Id);
            entity.HasIndex(entitlement => entitlement.SubscriptionId).IsUnique();
            entity.HasIndex(entitlement => new { entitlement.UserId, entitlement.Status, entitlement.EndsAt });
            entity.Property(entitlement => entitlement.PlanCodeSnapshot).HasMaxLength(40).IsRequired();
            entity.Property(entitlement => entitlement.Status).HasMaxLength(20).IsRequired();
            entity.Property(entitlement => entitlement.ConcurrencyToken).IsConcurrencyToken();
            entity.HasOne(entitlement => entitlement.User).WithMany().HasForeignKey(entitlement => entitlement.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(entitlement => entitlement.Subscription).WithOne(subscription => subscription.Entitlement)
                .HasForeignKey<Entitlement>(entitlement => entitlement.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<UsageEvent>(entity =>
        {
            entity.ToTable("usage_events");
            entity.HasKey(usage => usage.Id);
            entity.HasIndex(usage => new { usage.UserId, usage.Action, usage.IdempotencyKey }).IsUnique();
            entity.HasIndex(usage => new { usage.EntitlementId, usage.Action, usage.SourceId }).IsUnique();
            entity.Property(usage => usage.Action).HasMaxLength(20).IsRequired();
            entity.Property(usage => usage.SourceType).HasMaxLength(40).IsRequired();
            entity.Property(usage => usage.SourceId).HasMaxLength(160).IsRequired();
            entity.Property(usage => usage.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(usage => usage.Reason).HasMaxLength(500);
            entity.HasOne(usage => usage.User).WithMany().HasForeignKey(usage => usage.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(usage => usage.Entitlement).WithMany(entitlement => entitlement.UsageEvents)
                .HasForeignKey(usage => usage.EntitlementId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("idempotency_keys");
            entity.HasKey(record => record.Id);
            entity.HasIndex(record => new { record.ActorId, record.Operation, record.Key }).IsUnique();
            entity.Property(record => record.Operation).HasMaxLength(80).IsRequired();
            entity.Property(record => record.Key).HasMaxLength(128).IsRequired();
            entity.Property(record => record.RequestFingerprint).HasMaxLength(128).IsRequired();
        });
        builder.Entity<OutboxEvent>(entity =>
        {
            entity.ToTable("outbox_events");
            entity.HasKey(outbox => outbox.Id);
            entity.HasIndex(outbox => new { outbox.Status, outbox.CreatedAt });
            entity.Property(outbox => outbox.Type).HasMaxLength(120).IsRequired();
            entity.Property(outbox => outbox.AggregateType).HasMaxLength(80).IsRequired();
            entity.Property(outbox => outbox.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(outbox => outbox.Status).HasMaxLength(20).IsRequired();
        });
    }
}
