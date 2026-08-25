using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Auth;
using Nexora.Data.Auth;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Postgres must be supplied through secret configuration.");
        }

        services.AddDbContext<NexoraDbContext>(options => options.UseNpgsql(connectionString));
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

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(options => options.SigningKey.Length >= 32, "Authentication:Jwt:SigningKey must contain at least 32 characters.")
            .Validate(options => options.AccessTokenMinutes is >= 5 and <= 60, "AccessTokenMinutes must be between 5 and 60.")
            .Validate(options => options.RefreshTokenDays is >= 1 and <= 90, "RefreshTokenDays must be between 1 and 90.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IAuthService, IdentityAuthService>();
        return services;
    }
}
