using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Admin;
using Nexora.Business.Auth;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Data.Auth;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;
using Nexora.Data.Privacy;

namespace Nexora.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddData(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDataPersistence(configuration);
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 10;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<NexoraDbContext>()
          .AddSignInManager().AddDefaultTokenProviders();

        services.AddOptions<EmailVerificationOptions>()
            .Bind(configuration.GetSection(EmailVerificationOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var uri) &&
                                  (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
                "Authentication:EmailVerification:PublicUrl must be an absolute HTTP(S) URL.")
            .Validate(options => options.TokenLifespanHours is >= 1 and <= 72,
                "Authentication:EmailVerification:TokenLifespanHours must be between 1 and 72.")
            .ValidateOnStart();
        services.Configure<DataProtectionTokenProviderOptions>(options =>
        {
            var hours = configuration.GetValue<int?>($"{EmailVerificationOptions.SectionName}:TokenLifespanHours") ?? 24;
            options.TokenLifespan = TimeSpan.FromHours(hours);
        });

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(options => options.SigningKey.Length >= 32, "Authentication:Jwt:SigningKey must contain at least 32 characters.")
            .Validate(options => options.AccessTokenMinutes is >= 5 and <= 60, "AccessTokenMinutes must be between 5 and 60.")
            .Validate(options => options.RefreshTokenDays is >= 1 and <= 90, "RefreshTokenDays must be between 1 and 90.")
            .ValidateOnStart();
        services.AddScoped<IAuthService, IdentityAuthService>();
        services.AddScoped<IAdminService, AdminService>();
        return services;
    }

    public static IServiceCollection AddDataPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Postgres must be supplied through secret configuration.");
        }

        services.AddDbContext<NexoraDbContext>(options => options.UseNpgsql(connectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IFeatureEntitlementService, FeatureEntitlementService>();
        services.AddScoped<IScenarioService, ScenarioStarService>();
        services.AddScoped<IStarAttemptService, ScenarioStarService>();
        services.AddScoped<IProgressService, ScenarioStarService>();
        services.AddScoped<IScenarioStarJobProcessor, ScenarioStarService>();
        services.AddSingleton<IResumeContextBuilder, ResumeContextBuilder>();
        services.AddScoped<PracticeService>();
        services.AddScoped<IPracticeService>(provider => provider.GetRequiredService<PracticeService>());
        services.AddScoped<IPracticeJobProcessor>(provider => provider.GetRequiredService<PracticeService>());
        services.AddScoped<PrivacyService>();
        services.AddScoped<IPrivacyService>(provider => provider.GetRequiredService<PrivacyService>());
        services.AddScoped<IPrivacyJobProcessor>(provider => provider.GetRequiredService<PrivacyService>());
        return services;
    }
}
