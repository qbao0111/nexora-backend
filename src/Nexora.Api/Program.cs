using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Nexora.Api.Authorization;
using Nexora.Api.Contracts;
using Nexora.Api.Health;
using Nexora.Api.Infrastructure;
using Nexora.Business;
using Nexora.Business.Authorization;
using Nexora.Data;
using Nexora.Data.Auth;
using Nexora.Data.Identity;
using Nexora.Integrations;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBusiness();
builder.Services.AddData(builder.Configuration);
builder.Services.AddIntegrations(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var message = context.ModelState.SelectMany(entry => entry.Value?.Errors ?? [])
            .Select(error => error.ErrorMessage).FirstOrDefault() ?? "Dữ liệu yêu cầu không hợp lệ.";
        return new BadRequestObjectResult(new ApiErrorEnvelope(new ApiError("VALIDATION_ERROR", message, context.HttpContext.TraceIdentifier)));
    };
});

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var signingKey = jwt.SigningKey.Length >= 32 ? jwt.SigningKey : new string('0', 32);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,
        ValidateAudience = true,
        ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Email,
        RoleClaimType = ClaimTypes.Role
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var subject = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var stamp = context.Principal?.FindFirstValue(IdentityAuthService.SecurityStampClaim);
            var manager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = string.IsNullOrWhiteSpace(subject) ? null : await manager.FindByIdAsync(subject);
            if (user is null || string.IsNullOrWhiteSpace(stamp) || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
                context.Fail("Token has been revoked.");
        },
        OnChallenge = async context =>
        {
            context.HandleResponse();
            await ApiErrorWriter.WriteAsync(context.HttpContext, 401, "UNAUTHENTICATED", "Bạn cần đăng nhập để tiếp tục.");
        },
        OnForbidden = context => ApiErrorWriter.WriteAsync(context.HttpContext, 403, "FORBIDDEN", "Bạn không có quyền thực hiện thao tác này.")
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.Admin, policy => policy.RequireRole("Admin"));
    options.AddPolicy(AuthorizationPolicies.Owner, policy => policy.Requirements.Add(new OwnerRequirement()));
});
builder.Services.AddSingleton<IAuthorizationHandler, OwnerAuthorizationHandler>();

var origins = builder.Configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? [];
if (origins.Length > 0)
    builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
if (origins.Length > 0) app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapOpenApi("/openapi/{documentName}.json");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/api/v1/health", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });
app.MapControllers();
app.Run();

public partial class Program;
