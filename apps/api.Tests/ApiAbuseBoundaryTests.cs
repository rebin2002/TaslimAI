using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Controllers;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ApiAbuseBoundaryTests
{
    [Theory]
    [InlineData(typeof(MovieDirectorController), nameof(MovieDirectorController.CreateProposal))]
    [InlineData(typeof(MovieDialogueController), nameof(MovieDialogueController.QueueTake))]
    public void Provider_backed_movie_starts_use_expensive_ai_rate_policy(Type controllerType, string actionName)
    {
        var action = controllerType.GetMethod(actionName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.NotNull(action);
        Assert.NotEmpty(action!.GetCustomAttributes<HttpPostAttribute>(inherit: true));
        var policy = action.GetCustomAttribute<EnableRateLimitingAttribute>(inherit: true);

        Assert.NotNull(policy);
        Assert.Equal(RateLimiting.ExpensiveAi, policy!.PolicyName);
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>(inherit: true));
    }
}
