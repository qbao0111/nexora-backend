using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Authorization;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Xunit;

namespace Nexora.IntegrationTests;

public sealed class IdentityAdminApiTests
{
    [Fact]
    public async Task NewUserRegistrationReceivesUserRoleAndFreePlanEntitlementAndUserClaim()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        var email = $"onboard-{Guid.NewGuid():N}@example.test";
        using var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "New Onboarded User"
        });

        Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var userScope = factory.Services.CreateScope();
        var registeredUser = await userScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        Assert.NotNull(registeredUser);
        var userId = registeredUser!.Id;
        using var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        using var loginDoc = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        var accessToken = loginDoc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;

        // Token contains User role claim
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(accessToken);
        var roleClaims = jwt.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == "role").Select(c => c.Value).ToArray();
        Assert.Contains(RoleNames.User, roleClaims);
        Assert.DoesNotContain(RoleNames.Admin, roleClaims);

        // Database checks
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        Assert.True(user.IsActive);

        var roles = await userManager.GetRolesAsync(user);
        Assert.Contains(RoleNames.User, roles);

        // Free Entitlement check
        var entitlements = await db.Entitlements.Where(e => e.UserId == userId).ToListAsync();
        Assert.Single(entitlements);
        var freeEntitlement = entitlements[0];
        Assert.Equal("free", freeEntitlement.PlanCodeSnapshot);
        Assert.Equal(1, freeEntitlement.InterviewLimit);
        Assert.Equal(0, freeEntitlement.Consumed);
        Assert.Equal(0, freeEntitlement.Reserved);

        // Subscription check (no order id)
        var subscriptions = await db.Subscriptions.Where(s => s.UserId == userId).ToListAsync();
        Assert.Single(subscriptions);
        Assert.Null(subscriptions[0].OrderId);

        // No orders created for free onboarding
        Assert.Empty(await db.Orders.Where(o => o.UserId == userId).ToListAsync());

        // /api/v1/me endpoint returns Free plan entitlement summary
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var meResponse = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        using var meDoc = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        var billing = meDoc.RootElement.GetProperty("data").GetProperty("billing");
        var entitlementProp = billing.GetProperty("entitlement");
        Assert.Equal("free", entitlementProp.GetProperty("planCode").GetString());
        Assert.Equal(1, entitlementProp.GetProperty("available").GetInt32());
    }

    [Fact]
    public async Task ActivePaidEntitlementTakesPrecedenceOverFreeEntitlementInMeEndpoint()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        // Seed a paid entitlement
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            var plan = new Nexora.Data.Billing.Plan { Id = Guid.NewGuid(), Code = $"pro-test-{Guid.NewGuid():N}", Name = "Pro Plan", IsActive = true, CreatedAt = now };
            var price = new Nexora.Data.Billing.PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 199000, Currency = "VND", DurationDays = 30, InterviewQuota = 20, IsActive = true, CreatedAt = now };
            var subscription = new Nexora.Data.Billing.Subscription { Id = Guid.NewGuid(), UserId = account.UserId, Status = "active", StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now };
            var paidEntitlement = new Nexora.Data.Billing.Entitlement
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                SubscriptionId = subscription.Id,
                PlanCodeSnapshot = plan.Code,
                Status = "active",
                InterviewLimit = 20,
                StartsAt = subscription.StartsAt,
                EndsAt = subscription.EndsAt,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid()
            };
            db.AddRange(plan, price, subscription, paidEntitlement);
            await db.SaveChangesAsync();
        }

        using var meResponse = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        using var meDoc = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync());
        var entitlementProp = meDoc.RootElement.GetProperty("data").GetProperty("billing").GetProperty("entitlement");
        Assert.NotEqual("free", entitlementProp.GetProperty("planCode").GetString());
        Assert.Equal(20, entitlementProp.GetProperty("available").GetInt32());
    }

    [Fact]
    public async Task InactiveUserIsBlockedFromLoginRefreshAndExistingTokensAreRejected()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        var account = await RegisterAsync(client, "inactive-test@example.test", "Strong!Pass123");

        // Deactivate the user directly in DB
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == account.UserId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        // Login fails with 401 Unauthorized
        using var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "inactive-test@example.test",
            password = "Strong!Pass123"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);

        // Refresh fails with 401 Unauthorized
        using var refreshClient = factory.CreateHttpsClient(handleCookies: false);
        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", $"nexora.refresh={account.RefreshToken}");
        using var refreshResponse = await refreshClient.SendAsync(refreshRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);

        // Access token request is rejected via OnTokenValidated
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var meResponse = await client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, meResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedPasswordChangeSucceedsAndRevokesPreviousSessions()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        var account = await RegisterAsync(client, "pwd-change@example.test", "OldStrong!Pass123");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        // Wrong current password fails with 401 Unauthorized
        using var wrongCurrentResponse = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "WrongPassword123!",
            newPassword = "NewStrong!Pass123"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCurrentResponse.StatusCode);

        // Too short new password fails with 400 Validation
        using var shortNewResponse = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "OldStrong!Pass123",
            newPassword = "short"
        });
        Assert.Equal(HttpStatusCode.BadRequest, shortNewResponse.StatusCode);

        // Valid password change returns 204 NoContent
        using var successResponse = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "OldStrong!Pass123",
            newPassword = "NewStrong!Pass123"
        });
        Assert.Equal(HttpStatusCode.NoContent, successResponse.StatusCode);

        // Old refresh token is revoked
        using var refreshClient = factory.CreateHttpsClient(handleCookies: false);
        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", $"nexora.refresh={account.RefreshToken}");
        using var oldRefreshResponse = await refreshClient.SendAsync(refreshRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefreshResponse.StatusCode);

        // Login with old password fails
        using var oldLoginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "pwd-change@example.test",
            password = "OldStrong!Pass123"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLoginResponse.StatusCode);

        // Login with new password succeeds
        using var newLoginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "pwd-change@example.test",
            password = "NewStrong!Pass123"
        });
        Assert.Equal(HttpStatusCode.OK, newLoginResponse.StatusCode);
    }

    [Fact]
    public async Task AdminRolesCatalogueReturnsCanonicalRoles()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        var adminUser = await RegisterAsync(client, "admin-catalogue@example.test", "Strong!Pass123");
        var admin = await MakeAdminAsync(factory, adminUser.UserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);

        using var response = await client.GetAsync("/api/v1/admin/roles");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToArray();

        Assert.Contains(RoleNames.User, items);
        Assert.Contains(RoleNames.Admin, items);
    }

    [Fact]
    public async Task AdminCanUpdateUserRolesWithEnforcedGuardrailsAndAuditing()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        var adminUser = await RegisterAsync(client, "admin-user@example.test", "Strong!Pass123");
        var admin = await MakeAdminAsync(factory, adminUser.UserId);
        var targetUser = await RegisterAsync(client, "target-user@example.test", "Strong!Pass123");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);

        // 1. Removing RoleNames.User fails with 400
        using var removeUserRoleResponse = await client.PutAsJsonAsync($"/api/v1/admin/users/{targetUser.UserId}/roles", new
        {
            roles = new[] { RoleNames.Admin },
            reason = "Missing user role test"
        });
        Assert.Equal(HttpStatusCode.BadRequest, removeUserRoleResponse.StatusCode);

        // 2. Promoting target user to Admin succeeds
        using var promoteResponse = await client.PutAsJsonAsync($"/api/v1/admin/users/{targetUser.UserId}/roles", new
        {
            roles = new[] { RoleNames.User, RoleNames.Admin },
            reason = "Promotion to co-admin"
        });
        Assert.Equal(HttpStatusCode.OK, promoteResponse.StatusCode);
        using var promoteDoc = JsonDocument.Parse(await promoteResponse.Content.ReadAsStringAsync());
        var updatedRoles = promoteDoc.RootElement.GetProperty("data").GetProperty("roles").EnumerateArray()
            .Select(r => r.GetString()).ToArray();
        Assert.Contains(RoleNames.User, updatedRoles);
        Assert.Contains(RoleNames.Admin, updatedRoles);

        // 3. Self-demotion fails with 409 Conflict
        using var selfDemoteResponse = await client.PutAsJsonAsync($"/api/v1/admin/users/{adminUser.UserId}/roles", new
        {
            roles = new[] { RoleNames.User },
            reason = "Attempt self-demotion"
        });
        Assert.Equal(HttpStatusCode.Conflict, selfDemoteResponse.StatusCode);

        // 4. Demoting targetUser back to User (now that adminUser is active admin) succeeds
        using var demoteResponse = await client.PutAsJsonAsync($"/api/v1/admin/users/{targetUser.UserId}/roles", new
        {
            roles = new[] { RoleNames.User },
            reason = "Demotion back to user"
        });
        Assert.Equal(HttpStatusCode.OK, demoteResponse.StatusCode);

        // Check audit logs
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var targetIdStr = targetUser.UserId.ToString("N");
            var audits = await db.AdminAuditEvents.Where(a => a.TargetId == targetIdStr && a.Action == "user.roles.update").ToListAsync();
            Assert.NotEmpty(audits);
        }
    }

    [Fact]
    public async Task AdminCanUpdateUserStatusWithGuardrailsAndSessionRevocation()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        var adminUser = await RegisterAsync(client, "admin-status@example.test", "Strong!Pass123");
        var admin = await MakeAdminAsync(factory, adminUser.UserId);
        var targetUser = await RegisterAsync(client, "target-status@example.test", "Strong!Pass123");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);

        // 1. Self-deactivation fails with 409 Conflict
        using var selfDeactivateResponse = await client.PutAsJsonAsync($"/api/v1/admin/users/{adminUser.UserId}/status", new
        {
            active = false,
            reason = "Attempt self-deactivate"
        });
        Assert.Equal(HttpStatusCode.Conflict, selfDeactivateResponse.StatusCode);

        // 2. Deactivating target user succeeds
        using var deactivateResponse = await client.PutAsJsonAsync($"/api/v1/admin/users/{targetUser.UserId}/status", new
        {
            active = false,
            reason = "Deactivating abusive account"
        });
        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);
        using var deactDoc = JsonDocument.Parse(await deactivateResponse.Content.ReadAsStringAsync());
        Assert.False(deactDoc.RootElement.GetProperty("data").GetProperty("active").GetBoolean());

        // 3. Target user's refresh token was revoked
        using var refreshClient = factory.CreateHttpsClient(handleCookies: false);
        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        refreshRequest.Headers.Add("Cookie", $"nexora.refresh={targetUser.RefreshToken}");
        using var refreshResponse = await refreshClient.SendAsync(refreshRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);

        // 4. Target user login fails
        using var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "target-status@example.test",
            password = "Strong!Pass123"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);

        // 5. Reactivating target user succeeds
        using var reactivateResponse = await client.PutAsJsonAsync($"/api/v1/admin/users/{targetUser.UserId}/status", new
        {
            active = true,
            reason = "Reactivating account after appeal"
        });
        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);
        using var reactDoc = JsonDocument.Parse(await reactivateResponse.Content.ReadAsStringAsync());
        Assert.True(reactDoc.RootElement.GetProperty("data").GetProperty("active").GetBoolean());

        // Target user can now login
        using var reactLoginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "target-status@example.test",
            password = "Strong!Pass123"
        });
        Assert.Equal(HttpStatusCode.OK, reactLoginResponse.StatusCode);

        // Check audit logs
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var targetIdStr = targetUser.UserId.ToString("N");
            var audits = await db.AdminAuditEvents.Where(a => a.TargetId == targetIdStr).ToListAsync();
            Assert.Contains(audits, a => a.Action == "user.deactivate");
            Assert.Contains(audits, a => a.Action == "user.reactivate");
        }
    }

    [Fact]
    public async Task AdminUserListFiltersByRolePlanCodeEntitlementStateAndPaginatesCorrectly()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        var adminUser = await RegisterAsync(client, "admin-filter@example.test", "Strong!Pass123", "Admin Master");
        var admin = await MakeAdminAsync(factory, adminUser.UserId);
        var normalUser = await RegisterAsync(client, "searchme-user@example.test", "Strong!Pass123", "Searchable Alice");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);

        // 1. Search by case-insensitive email
        using var searchEmail = await client.GetAsync("/api/v1/admin/users?search=SEARCHME");
        Assert.Equal(HttpStatusCode.OK, searchEmail.StatusCode);
        using var docEmail = JsonDocument.Parse(await searchEmail.Content.ReadAsStringAsync());
        var itemsEmail = docEmail.RootElement.GetProperty("data").GetProperty("users").EnumerateArray().ToArray();
        Assert.Contains(itemsEmail, u => u.GetProperty("id").GetGuid() == normalUser.UserId);

        // 2. Search by case-insensitive display name
        using var searchName = await client.GetAsync("/api/v1/admin/users?search=alice");
        Assert.Equal(HttpStatusCode.OK, searchName.StatusCode);
        using var docName = JsonDocument.Parse(await searchName.Content.ReadAsStringAsync());
        var itemsName = docName.RootElement.GetProperty("data").GetProperty("users").EnumerateArray().ToArray();
        Assert.Single(itemsName);
        Assert.Equal(normalUser.UserId, itemsName[0].GetProperty("id").GetGuid());

        // 3. Filter by role = Admin
        using var filterAdmin = await client.GetAsync("/api/v1/admin/users?role=Admin");
        Assert.Equal(HttpStatusCode.OK, filterAdmin.StatusCode);
        using var docAdmin = JsonDocument.Parse(await filterAdmin.Content.ReadAsStringAsync());
        var itemsAdmin = docAdmin.RootElement.GetProperty("data").GetProperty("users").EnumerateArray().ToArray();
        Assert.All(itemsAdmin, u => Assert.Contains("Admin", u.GetProperty("roles").EnumerateArray().Select(r => r.GetString())));

        // 4. Filter by planCode = free
        using var filterFree = await client.GetAsync("/api/v1/admin/users?planCode=free");
        Assert.Equal(HttpStatusCode.OK, filterFree.StatusCode);
        using var docFree = JsonDocument.Parse(await filterFree.Content.ReadAsStringAsync());
        var itemsFree = docFree.RootElement.GetProperty("data").GetProperty("users").EnumerateArray().ToArray();
        Assert.Contains(itemsFree, u => u.GetProperty("id").GetGuid() == normalUser.UserId);

        // 5. Filter by entitlementState = active
        using var filterActive = await client.GetAsync("/api/v1/admin/users?entitlementState=active");
        Assert.Equal(HttpStatusCode.OK, filterActive.StatusCode);
        using var docActive = JsonDocument.Parse(await filterActive.Content.ReadAsStringAsync());
        var itemsActive = docActive.RootElement.GetProperty("data").GetProperty("users").EnumerateArray().ToArray();
        Assert.Contains(itemsActive, u => u.GetProperty("id").GetGuid() == normalUser.UserId);

        // 6. Pagination cursor correctness: pageSize = 1
        using var page1Response = await client.GetAsync("/api/v1/admin/users?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, page1Response.StatusCode);
        using var page1Doc = JsonDocument.Parse(await page1Response.Content.ReadAsStringAsync());
        var page1Items = page1Doc.RootElement.GetProperty("data").GetProperty("users").EnumerateArray().ToArray();
        var nextCursor = page1Doc.RootElement.GetProperty("data").GetProperty("lastId").GetString();
        Assert.Single(page1Items);
        Assert.NotNull(nextCursor);
        // The nextCursor must equal the last item's id!
        Assert.Equal(page1Items[0].GetProperty("id").GetString(), nextCursor);
    }

    private static async Task<Account> RegisterAsync(HttpClient client, string? email = null, string password = "Strong!Pass123", string displayName = "Candidate")
    {
        email ??= $"user-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password,
            displayName
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookieHeader = login.Headers.GetValues("Set-Cookie").FirstOrDefault(c => c.StartsWith("nexora.refresh=", StringComparison.Ordinal));
        var refreshToken = cookieHeader is not null ? cookieHeader.Split(';', 2)[0].Split('=', 2)[1] : "";
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!, refreshToken);
    }

    private static async Task<AdminAccount> MakeAdminAsync(NexoraApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await roleManager.RoleExistsAsync(RoleNames.Admin) is false)
            await roleManager.CreateAsync(new IdentityRole<Guid>(RoleNames.Admin));
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is not null && await userManager.IsInRoleAsync(user, RoleNames.Admin) is false)
            await userManager.AddToRoleAsync(user, RoleNames.Admin);

        using var client = factory.CreateHttpsClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user?.Email, password = "Strong!Pass123" });
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return new AdminAccount(json.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
    }

    private sealed record Account(Guid UserId, string AccessToken, string RefreshToken);
    private sealed record AdminAccount(string AccessToken);
}
