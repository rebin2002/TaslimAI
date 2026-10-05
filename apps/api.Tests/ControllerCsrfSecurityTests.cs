using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Taslim.Api.Controllers;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Keeps the controller-level CSRF contract aligned with the API middleware.
/// The completion bridge is the only intentional exception because it is
/// anonymous at the HTTP layer and authorized by its HMAC signature instead.
/// </summary>
public sealed class ControllerCsrfSecurityTests
{
    [Fact]
    public void Authenticated_state_changing_controller_actions_declare_antiforgery_protection()
    {
        var auditedActions = 0;

        foreach (var (controller, action) in StateChangingActions())
        {
            if (controller == typeof(AutopilotIntakeController)
                && action.Name == nameof(AutopilotIntakeController.Submit))
                continue;

            var allowsAnonymous = HasAttribute<AllowAnonymousAttribute>(controller, action);
            var requiresAuthentication = HasAttribute<AuthorizeAttribute>(controller, action);

            Assert.True(
                allowsAnonymous || requiresAuthentication,
                $"{controller.Name}.{action.Name} must declare authorization or an explicit security exception.");
            Assert.NotNull(
                action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>(inherit: true));
            auditedActions++;
        }

        Assert.True(auditedActions > 0, "The controller CSRF audit did not discover any state-changing actions.");
    }

    [Fact]
    public void Signature_authenticated_autopilot_webhook_is_an_explicit_antiforgery_exception()
    {
        var controller = typeof(AutopilotIntakeController);
        var action = controller.GetMethod(
            nameof(AutopilotIntakeController.Submit),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.NotNull(controller.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));
        Assert.NotNull(controller.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>(inherit: true));
        Assert.NotNull(action);
        Assert.NotEmpty(action!.GetCustomAttributes<HttpPostAttribute>(inherit: true));
        Assert.Null(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>(inherit: true));
    }

    private static IEnumerable<(Type Controller, MethodInfo Action)> StateChangingActions()
    {
        var assembly = typeof(AutopilotIntakeController).Assembly;
        return assembly
            .GetTypes()
            .Where(type => type.IsClass
                && !type.IsAbstract
                && type.Namespace == typeof(AutopilotIntakeController).Namespace
                && type.Name.EndsWith("Controller", StringComparison.Ordinal))
            .SelectMany(controller => controller
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(action => action
                    .GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                    .Any(attribute => attribute.HttpMethods.Any(IsStateChanging)))
                .Select(action => (controller, action)));
    }

    private static bool HasAttribute<TAttribute>(Type controller, MethodInfo action)
        where TAttribute : Attribute =>
        controller.GetCustomAttribute<TAttribute>(inherit: true) is not null
        || action.GetCustomAttribute<TAttribute>(inherit: true) is not null;

    private static bool IsStateChanging(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);
}
