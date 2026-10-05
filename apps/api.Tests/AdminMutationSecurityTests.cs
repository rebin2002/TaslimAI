using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Taslim.Api.Controllers;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AdminMutationSecurityTests
{
    public static IEnumerable<object[]> PrivilegedMutationActions()
    {
        yield return [typeof(AdminOperationsController), nameof(AdminOperationsController.Recover)];
        yield return [typeof(AutopilotConsoleController), nameof(AutopilotConsoleController.UpsertBacklog)];
        yield return [typeof(AutopilotConsoleController), nameof(AutopilotConsoleController.Control)];
        yield return [typeof(AutopilotConsoleController), nameof(AutopilotConsoleController.Reconcile)];
    }

    [Theory]
    [MemberData(nameof(PrivilegedMutationActions))]
    public void Privileged_mutations_declare_antiforgery_protection(Type controllerType, string actionName)
    {
        var action = controllerType.GetMethod(actionName, BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(action);
        Assert.NotEmpty(action!.GetCustomAttributes<HttpMethodAttribute>(inherit: true));
        Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>(inherit: true));
    }
}
