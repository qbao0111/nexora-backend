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
ProductionSafety.ValidateDevelopmentAdapters(
    builder.Environment.IsProduction(),
    builder.Configuration.GetValue("Features:Ai", true),
    builder.Configuration.GetValue("Features:Payment", true),
    builder.Configuration.GetValue("Features:Upload", true));
builder.Services.AddIntegrations(builder.Configuration);
builder.Services.AddControllers(options => options.Conventions.Add(new DevelopmentOnlyControllerConvention(builder.Environment)));
builder.Services.AddOpenApi(OpenApiConfiguration.Configure);
builder.Services.AddOptions<OperationsHealthOptions>()
    .Bind(builder.Configuration.GetSection(OperationsHealthOptions.SectionName))
    .Validate(options => options.MaxQueueLagMinutes > 0 && options.MaxPaymentPendingMinutes > 0 && options.RecentFailureWindowMinutes > 0,
        "Operations health thresholds must be positive.")
    .ValidateOnStart();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"])
    .AddCheck<OperationsHealthCheck>("operations", tags: ["operations"]);
builder.Services.AddHardening(builder.Configuration);
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
        RoleClaimType = "role"
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var subject = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var stamp = context.Principal?.FindFirstValue(IdentityAuthService.SecurityStampClaim);
            var manager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = string.IsNullOrWhiteSpace(subject) ? null : await manager.FindByIdAsync(subject);
            if (user is null || !user.IsActive || user.DeletionRequestedAt is not null || user.DeletedAt is not null ||
                string.IsNullOrWhiteSpace(stamp) || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
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
    options.AddPolicy(AuthorizationPolicies.Admin, policy => policy.RequireAssertion(context =>
        context.User.IsInRole(Nexora.Business.Authorization.RoleNames.Admin) ||
        context.User.HasClaim("role", Nexora.Business.Authorization.RoleNames.Admin) ||
        context.User.HasClaim(ClaimTypes.Role, Nexora.Business.Authorization.RoleNames.Admin)));
    options.AddPolicy(AuthorizationPolicies.Owner, policy => policy.Requirements.Add(new OwnerRequirement()));
});
builder.Services.AddSingleton<IAuthorizationHandler, OwnerAuthorizationHandler>();

builder.Services.AddCors(options => options.AddPolicy("Frontend", policy => policy
    .SetIsOriginAllowed(origin => (builder.Configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? [])
        .Contains(origin, StringComparer.OrdinalIgnoreCase))
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestTelemetryMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseMiddleware<FeatureGateMiddleware>();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapOpenApi("/openapi/{documentName}.json");
if (app.Environment.IsDevelopment())
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("../openapi/v1.json", "Nexora API v1");
        options.DocumentTitle = "Nexora API — Development";
        options.ConfigObject.PersistAuthorization = false;
        options.EnableValidator("");
    });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/api/v1/health", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });
app.MapHealthChecks("/api/v1/health/operations", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("operations") });
app.MapControllers();
app.Run();

public partial class Program;
