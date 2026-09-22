using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Taslim.Api.Domain;

namespace Taslim.Api.Authorization;

public static class AdminPolicies
{
    public const string Usage = "TaslimAdminUsage";
    public const string Role = "TaslimAdministrator";
}

public sealed class AdminUsageRequirement : IAuthorizationRequirement;

public sealed class AdminUsageAuthorizationHandler(
    UserManager<ApplicationUser> userManager) : AuthorizationHandler<AdminUsageRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminUsageRequirement requirement)
    {
        var value = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(value, out var userId)) return;
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is not null && user.IsActive && await userManager.IsInRoleAsync(user, AdminPolicies.Role))
            context.Succeed(requirement);
    }
}

public static class AdminRoleBootstrapper
{
    public static async Task EnsureConfiguredAdministratorsAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var configuredEmails = configuration.GetSection("Admin:BootstrapEmails").Get<string[]>() ?? [];
        if (configuredEmails.Length == 0) return;

        var roleManager = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roleManager.RoleExistsAsync(AdminPolicies.Role))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole<Guid>(AdminPolicies.Role));
            if (!roleResult.Succeeded)
                throw new InvalidOperationException("The administrator role could not be initialized.");
        }

        foreach (var email in configuredEmails.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                logger.LogWarning("A configured administrator account was not found.");
                continue;
            }

            if (!await userManager.IsInRoleAsync(user, AdminPolicies.Role))
            {
                var result = await userManager.AddToRoleAsync(user, AdminPolicies.Role);
                if (!result.Succeeded) throw new InvalidOperationException("A configured administrator could not be granted access.");
                logger.LogInformation("Configured administrator access granted. UserId={UserId}", user.Id);
            }
        }
    }
}
