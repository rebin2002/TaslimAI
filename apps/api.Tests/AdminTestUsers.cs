using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Xunit;

namespace Taslim.Api.Tests;

internal static class AdminTestUsers
{
    public static async Task GrantConfirmedRoleAsync(IServiceProvider services, string email)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (!await roles.RoleExistsAsync(AdminPolicies.Role))
            Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(AdminPolicies.Role))).Succeeded);
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        await ConfirmEmailAsync(users, user);
        Assert.True((await users.AddToRoleAsync(user, AdminPolicies.Role)).Succeeded);
    }

    public static async Task ConfirmEmailAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        Assert.True((await users.ConfirmEmailAsync(user, token)).Succeeded);
    }
}
