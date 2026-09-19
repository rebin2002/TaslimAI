using Microsoft.AspNetCore.Antiforgery;
using Taslim.Api.Contracts;

namespace Taslim.Api.Infrastructure;

public sealed class AntiforgeryValidationMiddleware(
    RequestDelegate next,
    IAntiforgery antiforgery,
    ILogger<AntiforgeryValidationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // All current state-changing /api endpoints are CSRF-protected. MVC's
        // [ValidateAntiForgeryToken] is a controller filter and is not always
        // present in endpoint metadata, so use the API boundary as the safe
        // enforcement scope while retaining the MVC attributes as defense in
        // depth.
        var endpointRequiresAntiforgery = context.Request.Path.StartsWithSegments("/api");
        var isStateChanging = HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method);

        if (endpointRequiresAntiforgery && isStateChanging && context.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                // Authentication has already populated HttpContext.User, so
                // Identity-bound tokens are validated against the right user.
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException exception)
            {
                logger.LogWarning(
                    "Antiforgery validation failed for {Method} {Path}. TraceId={TraceId}; Authenticated={Authenticated}; ErrorType={ErrorType}",
                    context.Request.Method,
                    context.Request.Path,
                    context.TraceIdentifier,
                    context.User.Identity?.IsAuthenticated == true,
                    exception.GetType().Name);

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new ErrorEnvelope(
                    new ErrorBody("CSRF_VALIDATION_FAILED", "Request validation failed.")));
                return;
            }
        }

        await next(context);
    }
}
