using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Identity;
using Nexora.Data.Learning;
using Nexora.Data.Practice;
using Nexora.Data.Privacy;

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
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<UploadIntentRecord> UploadIntents => Set<UploadIntentRecord>();
    public DbSet<ResumeRecord> Resumes => Set<ResumeRecord>();
    public DbSet<JobDescription> JobDescriptions => Set<JobDescription>();
    public DbSet<ResumeAnalysis> ResumeAnalyses => Set<ResumeAnalysis>();
    public DbSet<InterviewSession> InterviewSessions => Set<InterviewSession>();
    public DbSet<InterviewQuestion> InterviewQuestions => Set<InterviewQuestion>();
    public DbSet<InterviewAnswer> InterviewAnswers => Set<InterviewAnswer>();
    public DbSet<InterviewReport> InterviewReports => Set<InterviewReport>();
    public DbSet<DataPrivacyRequest> DataPrivacyRequests => Set<DataPrivacyRequest>();
    public DbSet<FeatureDefinition> FeatureDefinitions => Set<FeatureDefinition>();
    public DbSet<PlanPriceFeature> PlanPriceFeatures => Set<PlanPriceFeature>();
    public DbSet<EntitlementFeature> EntitlementFeatures => Set<EntitlementFeature>();
    public DbSet<FeatureUsageEvent> FeatureUsageEvents => Set<FeatureUsageEvent>();
    public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();
    public DbSet<ScenarioCategory> ScenarioCategories => Set<ScenarioCategory>();
    public DbSet<Scenario> Scenarios => Set<Scenario>();
    public DbSet<ScenarioAttempt> ScenarioAttempts => Set<ScenarioAttempt>();
    public DbSet<StarAttempt> StarAttempts => Set<StarAttempt>();
    public DbSet<CareerGoal> CareerGoals => Set<CareerGoal>();
    public DbSet<LearningPath> LearningPaths => Set<LearningPath>();
    public DbSet<LearningPathMilestone> LearningPathMilestones => Set<LearningPathMilestone>();
    public DbSet<LearningPathActivity> LearningPathActivities => Set<LearningPathActivity>();
    public DbSet<Nexora.Data.Realtime.RealtimeNotification> RealtimeNotifications => Set<Nexora.Data.Realtime.RealtimeNotification>();

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
            entity.Property(user => user.IsActive).IsRequired().HasDefaultValue(true);
        });
        builder.Entity<IdentityRole<Guid>>(entity =>
        {
            entity.HasData(
                new IdentityRole<Guid>
                {
                    Id = Guid.Parse("50000000-0000-0000-0000-000000000001"),
                    Name = Nexora.Business.Authorization.RoleNames.User,
                    NormalizedName = Nexora.Business.Authorization.RoleNames.User.ToUpperInvariant(),
                    ConcurrencyStamp = "50000000-0000-0000-0000-000000000001"
                },
                new IdentityRole<Guid>
                {
                    Id = Guid.Parse("50000000-0000-0000-0000-000000000002"),
                    Name = Nexora.Business.Authorization.RoleNames.Admin,
                    NormalizedName = Nexora.Business.Authorization.RoleNames.Admin.ToUpperInvariant(),
                    ConcurrencyStamp = "50000000-0000-0000-0000-000000000002"
                }
            );
        });
        builder.Entity<UserProfile>(entity =>
        {
            entity.ToTable("user_profiles");
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => profile.UserId).IsUnique();
            entity.HasIndex(profile => profile.PrimaryResumeId);
            entity.Property(profile => profile.DisplayName).HasMaxLength(120);
            entity.HasOne(profile => profile.User).WithOne(user => user.Profile)
                .HasForeignKey<UserProfile>(profile => profile.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(profile => profile.PrimaryResume).WithMany()
                .HasForeignKey(profile => profile.PrimaryResumeId).OnDelete(DeleteBehavior.SetNull);
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
        ConfigurePractice(builder);
        ConfigurePrivacy(builder);
        ConfigureFeatureManagement(builder);
        ConfigureScenarioStar(builder);
        ConfigureCareerGoals(builder);
        ConfigureLearningPaths(builder);
        builder.Entity<Nexora.Data.Realtime.RealtimeNotification>(entity =>
        {
            entity.ToTable("realtime_notifications");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ResourceType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.HasIndex(item => new { item.ProcessedAt, item.CreatedAt });
        });
    }

    private static void ConfigureFeatureManagement(ModelBuilder builder)
    {
        builder.Entity<FeatureDefinition>(entity =>
        {
            entity.ToTable("feature_definitions");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Code).IsUnique();
            entity.Property(item => item.Code).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(500);
            var createdAt = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
            entity.HasData(
                new FeatureDefinition { Id = Guid.Parse("20000000-0000-0000-0000-000000000001"), Code = "cv_analysis", Name = "Phân tích CV", Description = "Phân tích CV và Job Description", SortOrder = 0, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt },
                new FeatureDefinition { Id = Guid.Parse("20000000-0000-0000-0000-000000000002"), Code = "interview", Name = "Phỏng vấn", Description = "Luyện phỏng vấn mô phỏng", SortOrder = 1, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt },
                new FeatureDefinition { Id = Guid.Parse("20000000-0000-0000-0000-000000000003"), Code = "scenario", Name = "Tình huống", Description = "Luyện tình huống thực tế", SortOrder = 2, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt },
                new FeatureDefinition { Id = Guid.Parse("20000000-0000-0000-0000-000000000004"), Code = "star_builder", Name = "STAR Builder", Description = "Xây dựng câu trả lời STAR", SortOrder = 3, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt },
                new FeatureDefinition { Id = Guid.Parse("20000000-0000-0000-0000-000000000005"), Code = "advanced_report", Name = "Báo cáo nâng cao", Description = "Báo cáo chi tiết và phân tích sâu", SortOrder = 4, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt },
                new FeatureDefinition { Id = Guid.Parse("20000000-0000-0000-0000-000000000006"), Code = "progress_analytics", Name = "Phân tích tiến độ", Description = "Theo dõi tiến độ luyện tập", SortOrder = 5, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt },
                new FeatureDefinition { Id = Guid.Parse("20000000-0000-0000-0000-000000000007"), Code = "interview_question_limit", Name = "Giới hạn câu hỏi phỏng vấn", Description = "Số câu hỏi tối đa trong mỗi phiên phỏng vấn", SortOrder = 6, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt });
        });
        builder.Entity<EntitlementFeature>(entity =>
        {
            entity.ToTable("entitlement_features");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.EntitlementId, item.FeatureDefinitionId }).IsUnique();
            entity.Property(item => item.FeatureCode).HasMaxLength(40).IsRequired();
            entity.Property(item => item.ConcurrencyToken).IsConcurrencyToken();
            entity.HasOne(item => item.Entitlement).WithMany().HasForeignKey(item => item.EntitlementId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.FeatureDefinition).WithMany().HasForeignKey(item => item.FeatureDefinitionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<FeatureUsageEvent>(entity =>
        {
            entity.ToTable("feature_usage_events");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.UserId, item.Action, item.IdempotencyKey }).IsUnique();
            entity.HasIndex(item => new { item.EntitlementFeatureId, item.Action, item.SourceId }).IsUnique();
            entity.Property(item => item.FeatureCode).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Action).HasMaxLength(20).IsRequired();
            entity.Property(item => item.SourceType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.SourceId).HasMaxLength(160).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(item => item.Reason).HasMaxLength(500);
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.EntitlementFeature).WithMany(ef => ef.UsageEvents).HasForeignKey(item => item.EntitlementFeatureId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AdminAuditEvent>(entity =>
        {
            entity.ToTable("admin_audit_events");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.CreatedAt);
            entity.Property(item => item.Action).HasMaxLength(80).IsRequired();
            entity.Property(item => item.TargetType).HasMaxLength(40).IsRequired();
            entity.Property(item => item.TargetId).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Reason).HasMaxLength(500).IsRequired();
            entity.Property(item => item.SafeMetadataJson).HasColumnType("jsonb");
            entity.HasOne(item => item.AdminUser).WithMany().HasForeignKey(item => item.AdminUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureScenarioStar(ModelBuilder builder)
    {
        builder.Entity<ScenarioCategory>(entity =>
        {
            entity.ToTable("scenario_categories");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Slug).IsUnique();
            entity.Property(item => item.Slug).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Name).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(500);
            var createdAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            entity.HasData(
                new ScenarioCategory { Id = Guid.Parse("40000000-0000-0000-0000-000000000001"), Slug = "banking", Name = "Ngân hàng", Description = "Tình huống ngành ngân hàng (demo)", SortOrder = 0, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt },
                new ScenarioCategory { Id = Guid.Parse("40000000-0000-0000-0000-000000000002"), Slug = "ecommerce", Name = "Thương mại điện tử", Description = "Tình huống ngành TMĐT (demo)", SortOrder = 1, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt },
                new ScenarioCategory { Id = Guid.Parse("40000000-0000-0000-0000-000000000003"), Slug = "logistics", Name = "Logistics", Description = "Tình huống ngành logistics (demo)", SortOrder = 2, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt });
        });
        builder.Entity<Scenario>(entity =>
        {
            entity.ToTable("scenarios");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Slug).IsUnique();
            entity.HasIndex(item => new { item.CategoryId, item.Status });
            entity.Property(item => item.Slug).HasMaxLength(120).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Summary).HasMaxLength(500).IsRequired();
            entity.Property(item => item.Difficulty).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Competency).HasMaxLength(80).IsRequired();
            entity.Property(item => item.Content).HasMaxLength(20_000).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.HasOne(item => item.Category).WithMany(cat => cat.Scenarios).HasForeignKey(item => item.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ScenarioAttempt>(entity =>
        {
            entity.ToTable("scenario_attempts");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.UserId, item.CreatedAt });
            entity.HasIndex(item => new { item.ScenarioId, item.UserId });
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.Property(item => item.Answer).HasMaxLength(12_000);
            entity.Property(item => item.EvaluationJson).HasColumnType("jsonb");
            entity.Property(item => item.ModelVersion).HasMaxLength(80);
            entity.Property(item => item.PromptVersion).HasMaxLength(80);
            entity.Property(item => item.SchemaVersion).HasMaxLength(80);
            entity.Property(item => item.ErrorCode).HasMaxLength(80);
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.Scenario).WithMany(s => s.Attempts).HasForeignKey(item => item.ScenarioId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<StarAttempt>(entity =>
        {
            entity.ToTable("star_attempts");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.UserId, item.CreatedAt });
            entity.Property(item => item.Question).HasMaxLength(2_000).IsRequired();
            entity.Property(item => item.Answer).HasMaxLength(12_000).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.Property(item => item.EvaluationJson).HasColumnType("jsonb");
            entity.Property(item => item.ModelVersion).HasMaxLength(80);
            entity.Property(item => item.PromptVersion).HasMaxLength(80);
            entity.Property(item => item.SchemaVersion).HasMaxLength(80);
            entity.Property(item => item.ErrorCode).HasMaxLength(80);
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
        });
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
            entity.Property(plan => plan.Description).HasMaxLength(500);
            entity.Property(plan => plan.Badge).HasMaxLength(40);
            var createdAt = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
            entity.HasData(
                new Plan { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Code = "free", Name = "Free", Description = "Dùng thử cơ bản", Badge = null, IsHighlighted = false, SortOrder = 0, IsActive = true, CreatedAt = createdAt },
                new Plan { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Code = "basic", Name = "Basic", Description = "Luyện tập cơ bản", Badge = null, IsHighlighted = false, SortOrder = 1, IsActive = true, CreatedAt = createdAt },
                new Plan { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Code = "weekly", Name = "Weekly", Description = "Luyện tập trong tuần", Badge = "popular", IsHighlighted = true, SortOrder = 2, IsActive = true, CreatedAt = createdAt },
                new Plan { Id = Guid.Parse("00000000-0000-0000-0000-000000000004"), Code = "pro", Name = "Pro", Description = "Trải nghiệm đầy đủ", Badge = null, IsHighlighted = false, SortOrder = 3, IsActive = true, CreatedAt = createdAt });
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
        builder.Entity<PlanPriceFeature>(entity =>
        {
            entity.ToTable("plan_price_features");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.PlanPriceId, item.FeatureDefinitionId }).IsUnique();
            entity.Property(item => item.Limit).HasDefaultValue(null);
            entity.HasOne(item => item.PlanPrice).WithMany(price => price.Features).HasForeignKey(item => item.PlanPriceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.FeatureDefinition).WithMany(fd => fd.PlanPriceFeatures).HasForeignKey(item => item.FeatureDefinitionId).OnDelete(DeleteBehavior.Restrict);
            var featureCreatedAt = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
            entity.HasData(
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000001"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000001"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000001"), IsEnabled = true, Limit = 1, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000002"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000001"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000002"), IsEnabled = true, Limit = 1, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000003"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000002"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000001"), IsEnabled = true, Limit = 3, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000004"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000002"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000002"), IsEnabled = true, Limit = 3, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000005"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000002"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000003"), IsEnabled = true, Limit = 3, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000006"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000002"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000004"), IsEnabled = true, Limit = 5, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000007"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000003"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000001"), IsEnabled = true, Limit = 5, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000008"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000003"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000002"), IsEnabled = true, Limit = 20, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000009"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000003"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000003"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000010"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000003"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000004"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000011"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000003"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000005"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000012"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000004"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000001"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000013"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000004"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000002"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000014"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000004"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000003"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000015"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000004"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000004"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000016"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000004"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000005"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000017"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000004"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000006"), IsEnabled = true, Limit = null, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000018"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000001"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000007"), IsEnabled = true, Limit = 3, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000019"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000002"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000007"), IsEnabled = true, Limit = 6, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000020"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000003"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000007"), IsEnabled = true, Limit = 8, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt },
                new PlanPriceFeature { Id = Guid.Parse("30000000-0000-0000-0000-000000000021"), PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000004"), FeatureDefinitionId = Guid.Parse("20000000-0000-0000-0000-000000000007"), IsEnabled = true, Limit = 10, CreatedAt = featureCreatedAt, UpdatedAt = featureCreatedAt });
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
            entity.Property(order => order.FeaturesSnapshot).HasColumnType("jsonb").IsRequired();
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

    private static void ConfigurePractice(ModelBuilder builder)
    {
        builder.Entity<StoredFile>(entity =>
        {
            entity.ToTable("stored_files");
            entity.HasKey(file => file.Id);
            entity.HasIndex(file => new { file.UserId, file.CreatedAt });
            entity.HasIndex(file => file.StorageKey).IsUnique();
            entity.Property(file => file.StorageKey).HasMaxLength(500).IsRequired();
            entity.Property(file => file.FileName).HasMaxLength(255).IsRequired();
            entity.Property(file => file.ContentType).HasMaxLength(120).IsRequired();
            entity.Property(file => file.Checksum).HasMaxLength(64).IsRequired();
            entity.HasOne(file => file.User).WithMany().HasForeignKey(file => file.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<UploadIntentRecord>(entity =>
        {
            entity.ToTable("upload_intents");
            entity.HasKey(intent => intent.Id);
            entity.HasIndex(intent => intent.TokenHash).IsUnique();
            entity.HasIndex(intent => intent.StorageKey).IsUnique();
            entity.HasIndex(intent => new { intent.UserId, intent.ExpiresAt });
            entity.Property(intent => intent.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(intent => intent.StorageKey).HasMaxLength(512).IsRequired();
            entity.Property(intent => intent.FileName).HasMaxLength(255).IsRequired();
            entity.Property(intent => intent.ContentType).HasMaxLength(120).IsRequired();
            entity.Property(intent => intent.Version).IsConcurrencyToken();
            entity.Property(intent => intent.Checksum).HasMaxLength(64);
            entity.HasOne(intent => intent.User).WithMany().HasForeignKey(intent => intent.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<ResumeRecord>(entity =>
        {
            entity.ToTable("resumes");
            entity.HasKey(resume => resume.Id);
            entity.HasIndex(resume => new { resume.UserId, resume.CreatedAt });
            entity.HasIndex(resume => resume.StoredFileId).IsUnique();
            entity.Property(resume => resume.Status).HasMaxLength(20).IsRequired();
            entity.Property(resume => resume.StructuredProfile).HasColumnType("jsonb");
            entity.Property(resume => resume.ProfileModelVersion).HasMaxLength(80);
            entity.Property(resume => resume.ProfilePromptVersion).HasMaxLength(80);
            entity.Property(resume => resume.ProfileSchemaVersion).HasMaxLength(80);
            entity.HasOne(resume => resume.User).WithMany().HasForeignKey(resume => resume.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(resume => resume.StoredFile).WithOne().HasForeignKey<ResumeRecord>(resume => resume.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<JobDescription>(entity =>
        {
            entity.ToTable("job_descriptions");
            entity.HasKey(jd => jd.Id);
            entity.HasIndex(jd => new { jd.UserId, jd.CreatedAt });
            entity.Property(jd => jd.Title).HasMaxLength(160).IsRequired();
            entity.Property(jd => jd.Content).HasMaxLength(30_000).IsRequired();
            entity.HasOne(jd => jd.User).WithMany().HasForeignKey(jd => jd.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ResumeAnalysis>(entity =>
        {
            entity.ToTable("resume_analyses", table =>
            {
                table.HasCheckConstraint(
                    "CK_resume_analyses_mode",
                    "\"Mode\" IN ('job_targeted', 'field_benchmark')");
                table.HasCheckConstraint(
                    "CK_resume_analyses_mode_job_description",
                    "((\"Mode\" = 'job_targeted' AND \"JobDescriptionId\" IS NOT NULL AND \"JobDescriptionVersion\" IS NOT NULL) OR (\"Mode\" = 'field_benchmark' AND \"JobDescriptionId\" IS NULL AND \"JobDescriptionVersion\" IS NULL))");
            });
            entity.HasKey(analysis => analysis.Id);
            entity.HasIndex(analysis => new { analysis.UserId, analysis.CreatedAt });
            entity.Property(analysis => analysis.Mode).HasMaxLength(32).IsRequired();
            entity.Property(analysis => analysis.ContextJson).HasColumnType("jsonb");
            entity.Property(analysis => analysis.Status).HasMaxLength(20).IsRequired();
            entity.Property(analysis => analysis.ModelVersion).HasMaxLength(80).IsRequired();
            entity.Property(analysis => analysis.PromptVersion).HasMaxLength(80).IsRequired();
            entity.Property(analysis => analysis.SchemaVersion).HasMaxLength(80).IsRequired();
            entity.Property(analysis => analysis.RubricVersion).HasMaxLength(80);
            entity.Property(analysis => analysis.ProfileSnapshot).HasColumnType("jsonb");
            entity.Property(analysis => analysis.ProfileModelVersion).HasMaxLength(80);
            entity.Property(analysis => analysis.ProfilePromptVersion).HasMaxLength(80);
            entity.Property(analysis => analysis.ProfileSchemaVersion).HasMaxLength(80);
            entity.Property(analysis => analysis.Result).HasColumnType("jsonb");
            entity.Property(analysis => analysis.ErrorCode).HasMaxLength(80);
            entity.HasOne(analysis => analysis.User).WithMany().HasForeignKey(analysis => analysis.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(analysis => analysis.Resume).WithMany().HasForeignKey(analysis => analysis.ResumeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(analysis => analysis.JobDescription).WithMany().HasForeignKey(analysis => analysis.JobDescriptionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<InterviewSession>(entity =>
        {
            entity.ToTable("interview_sessions");
            entity.HasKey(session => session.Id);
            entity.HasIndex(session => new { session.UserId, session.CreatedAt });
            entity.HasIndex(session => session.CareerGoalId);
            entity.HasIndex(session => session.SourceInterviewId);
            entity.HasIndex(session => session.SourceQuestionId);
            entity.HasIndex(session => session.ReservationEventId).IsUnique();
            entity.Property(session => session.Role).HasMaxLength(160).IsRequired();
            entity.Property(session => session.Seniority).HasMaxLength(40).IsRequired();
            entity.Property(session => session.InterviewType).HasMaxLength(40).IsRequired();
            entity.Property(session => session.Difficulty).HasMaxLength(40).IsRequired();
            entity.Property(session => session.Status).HasMaxLength(20).IsRequired();
            entity.Property(session => session.PracticeReason).HasMaxLength(40);
            entity.Property(session => session.FocusTopic).HasMaxLength(80);
            entity.Property(session => session.Version).IsConcurrencyToken();
            entity.HasOne(session => session.User).WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.Resume).WithMany().HasForeignKey(session => session.ResumeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.JobDescription).WithMany().HasForeignKey(session => session.JobDescriptionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CareerGoal>().WithMany().HasForeignKey(session => session.CareerGoalId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<InterviewSession>().WithMany().HasForeignKey(session => session.SourceInterviewId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<InterviewQuestion>().WithMany().HasForeignKey(session => session.SourceQuestionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(session => session.ReservationEvent).WithOne().HasForeignKey<InterviewSession>(session => session.ReservationEventId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<InterviewQuestion>(entity =>
        {
            entity.ToTable("interview_questions", table =>
            {
                table.HasCheckConstraint(
                    "CK_interview_questions_kind",
                    "\"Kind\" IN ('primary', 'followup')");
                table.HasCheckConstraint(
                    "CK_interview_questions_parent",
                    "((\"Kind\" = 'primary' AND \"ParentQuestionId\" IS NULL) OR (\"Kind\" = 'followup' AND \"ParentQuestionId\" IS NOT NULL))");
            });
            entity.HasKey(question => question.Id);
            entity.HasIndex(question => new { question.InterviewSessionId, question.Sequence }).IsUnique();
            entity.HasIndex(question => question.ParentQuestionId);
            entity.Property(question => question.Kind).HasMaxLength(16).IsRequired();
            entity.Property(question => question.Topic).HasMaxLength(80).IsRequired();
            entity.Property(question => question.Content).HasMaxLength(2_000).IsRequired();
            entity.Property(question => question.PromptVersion).HasMaxLength(80).IsRequired();
            entity.Property(question => question.ModelVersion).HasMaxLength(80).IsRequired();
            entity.HasOne(question => question.InterviewSession).WithMany(session => session.Questions)
                .HasForeignKey(question => question.InterviewSessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(question => question.ParentQuestion).WithMany(question => question.FollowUps)
                .HasForeignKey(question => question.ParentQuestionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<InterviewAnswer>(entity =>
        {
            entity.ToTable("interview_answers");
            entity.HasKey(answer => answer.Id);
            entity.HasIndex(answer => new { answer.InterviewSessionId, answer.QuestionId }).IsUnique();
            entity.HasIndex(answer => new { answer.UserId, answer.CreatedAt });
            entity.Property(answer => answer.Content).HasMaxLength(12_000).IsRequired();
            entity.Property(answer => answer.Evaluation).HasColumnType("jsonb").IsRequired();
            entity.HasOne(answer => answer.User).WithMany().HasForeignKey(answer => answer.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(answer => answer.InterviewSession).WithMany(session => session.Answers)
                .HasForeignKey(answer => answer.InterviewSessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(answer => answer.Question).WithOne(question => question.Answer)
                .HasForeignKey<InterviewAnswer>(answer => answer.QuestionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<InterviewReport>(entity =>
        {
            entity.ToTable("interview_reports");
            entity.HasKey(report => report.Id);
            entity.HasIndex(report => report.InterviewSessionId).IsUnique();
            entity.HasIndex(report => new { report.UserId, report.CreatedAt });
            entity.Property(report => report.Rubric).HasColumnType("jsonb").IsRequired();
            entity.Property(report => report.Strengths).HasColumnType("jsonb").IsRequired();
            entity.Property(report => report.Gaps).HasColumnType("jsonb").IsRequired();
            entity.Property(report => report.ActionPlan).HasColumnType("jsonb").IsRequired();
            entity.Property(report => report.Disclaimer).HasMaxLength(500).IsRequired();
            entity.Property(report => report.ModelVersion).HasMaxLength(80).IsRequired();
            entity.Property(report => report.PromptVersion).HasMaxLength(80).IsRequired();
            entity.Property(report => report.RubricVersion).HasMaxLength(80).IsRequired();
            entity.Property(report => report.SchemaVersion).HasMaxLength(80).IsRequired();
            entity.HasOne(report => report.User).WithMany().HasForeignKey(report => report.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(report => report.InterviewSession).WithOne(session => session.Report)
                .HasForeignKey<InterviewReport>(report => report.InterviewSessionId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePrivacy(ModelBuilder builder)
    {
        builder.Entity<DataPrivacyRequest>(entity =>
        {
            entity.ToTable("data_privacy_requests");
            entity.HasKey(request => request.Id);
            entity.HasIndex(request => new { request.UserId, request.IdempotencyKey }).IsUnique();
            entity.HasIndex(request => new { request.Status, request.NextAttemptAt });
            entity.Property(request => request.Type).HasMaxLength(40).IsRequired();
            entity.Property(request => request.Status).HasMaxLength(20).IsRequired();
            entity.Property(request => request.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(request => request.ErrorCode).HasMaxLength(80);
        });
    }

    private static void ConfigureCareerGoals(ModelBuilder builder)
    {
        builder.Entity<CareerGoal>(entity =>
        {
            entity.ToTable("career_goals");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.UserId, item.CreatedAt });
            entity.HasIndex(item => item.TargetJobDescriptionId);
            entity.HasIndex(item => item.UserId)
                .IsUnique()
                .HasDatabaseName("IX_career_goals_one_active_per_user")
                .HasFilter("\"Active\" = TRUE");
            entity.Property(item => item.TargetRole).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Seniority).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Industry).HasMaxLength(120);
            entity.Property(item => item.TargetCompany).HasMaxLength(160);
            entity.Property(item => item.TargetDate).HasColumnType("date");
            entity.Property(item => item.Active).IsRequired().HasDefaultValue(true);
            entity.Property(item => item.DeletedAt);
            entity.Property(item => item.CreatedAt).IsRequired();
            entity.Property(item => item.UpdatedAt).IsRequired();
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.TargetJobDescription).WithMany().HasForeignKey(item => item.TargetJobDescriptionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureLearningPaths(ModelBuilder builder)
    {
        builder.Entity<LearningPath>(entity =>
        {
            entity.ToTable("learning_paths");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.UserId, item.CareerGoalId })
                .IsUnique()
                .HasDatabaseName("IX_learning_paths_one_per_user_career_goal");
            entity.HasIndex(item => new { item.UserId, item.UpdatedAt });
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.Property(item => item.CreatedAt).IsRequired();
            entity.Property(item => item.UpdatedAt).IsRequired();
            entity.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(item => item.CareerGoal).WithMany().HasForeignKey(item => item.CareerGoalId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<LearningPathMilestone>(entity =>
        {
            entity.ToTable("learning_path_milestones");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.LearningPathId, item.Code }).IsUnique();
            entity.HasIndex(item => new { item.LearningPathId, item.SortOrder });
            entity.Property(item => item.Code).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.Property(item => item.CreatedAt).IsRequired();
            entity.Property(item => item.UpdatedAt).IsRequired();
            entity.HasOne(item => item.LearningPath).WithMany(path => path.Milestones)
                .HasForeignKey(item => item.LearningPathId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<LearningPathActivity>(entity =>
        {
            entity.ToTable("learning_path_activities");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.LearningPathId, item.ActivityKey }).IsUnique();
            entity.HasIndex(item => new { item.LearningPathId, item.Status, item.SortOrder });
            entity.HasIndex(item => item.ResourceId);
            entity.Property(item => item.ActivityKey).HasMaxLength(160).IsRequired();
            entity.Property(item => item.Type).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Title).HasMaxLength(200).IsRequired();
            entity.Property(item => item.Description).HasMaxLength(500).IsRequired();
            entity.Property(item => item.CompetencyCode).HasMaxLength(120);
            entity.Property(item => item.ExternalUrl).HasMaxLength(2_048);
            entity.Property(item => item.Priority).IsRequired();
            entity.Property(item => item.SortOrder).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(20).IsRequired();
            entity.Property(item => item.CreatedAt).IsRequired();
            entity.Property(item => item.UpdatedAt).IsRequired();
            entity.HasOne(item => item.LearningPath).WithMany()
                .HasForeignKey(item => item.LearningPathId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(item => item.Milestone).WithMany(milestone => milestone.Activities)
                .HasForeignKey(item => item.LearningPathMilestoneId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
