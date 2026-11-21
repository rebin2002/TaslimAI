using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Taslim.Api.Contracts;

namespace Taslim.Api.Infrastructure;

/// <summary>
/// Normalizes the double-submit rejections that are raised by the MVC
/// <c>[ValidateAntiForgeryToken]</c> filter so they carry the same machine-readable
/// envelope as the rejections raised earlier by <see cref="AntiforgeryValidationMiddleware"/>.
///
/// The middleware enumerates the anonymous authentication endpoints by exact path,
/// while MVC routing additionally accepts trailing-separator and case variants of
/// those same endpoints. A variant such as <c>POST /api/auth/login/</c> therefore
/// skips the middleware and is rejected by MVC, which by default answers with the
/// generic Problem Details body that carries no <c>error.code</c>. The web API
/// client only performs its single refresh-and-retry when it recognizes
/// <c>CSRF_VALIDATION_FAILED</c>, so a rejection whose code is missing becomes an
/// unrecoverable client-side failure instead of a transparent token refresh.
///
/// This keeps the rejection contract independent of path spelling: every rejected
/// state-changing antiforgery request returns the same safe envelope regardless of
/// which enforcement layer observed it first.
/// </summary>
public sealed class AntiforgeryFailureEnvelopeFilter(
    ILogger<AntiforgeryFailureEnvelopeFilter> logger) : IAlwaysRunResultFilter
{
    /// <summary>
    /// Runs before MVC maps client-error results onto Problem Details, which happens at
    /// order -2000 and erases the concrete rejection type. Keeping the rewrite ahead of
    /// that mapping is what allows the stable <c>error.code</c> envelope to win.
    /// </summary>
    public const int EnvelopeOrder = -2100;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not AntiforgeryValidationFailedResult)
        {
            return;
        }

        var request = context.HttpContext.Request;
        // Safe diagnostics only: method, path, trace id, authentication state and
        // the rejection type. Tokens, cookies and request bodies are never logged.
        logger.LogWarning(
            "Antiforgery validation failed for {Method} {Path}. TraceId={TraceId}; Authenticated={Authenticated}; ErrorType={ErrorType}",
            request.Method,
            request.Path,
            context.HttpContext.TraceIdentifier,
            context.HttpContext.User.Identity?.IsAuthenticated == true,
            nameof(AntiforgeryValidationFailedResult));

        context.Result = new BadRequestObjectResult(new ErrorEnvelope(
            new ErrorBody("CSRF_VALIDATION_FAILED", "Request validation failed.")))
        {
            ContentTypes = { "application/json" },
        };
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
