using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Autopilot;
using Taslim.Api.Infrastructure;

namespace Taslim.Api.Controllers;

/// <summary>
/// Taslim completion bridge intake. Events are authenticated with an HMAC
/// signature, protected against replay, deduplicated by idempotency key, and
/// persisted into the durable completion queue before any orchestration runs.
/// The endpoint is anonymous at the HTTP layer and authorized by signature only.
/// </summary>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/autopilot")]
public sealed class AutopilotIntakeController(IAutopilotEventIntake intake) : ControllerBase
{
    public const string SignatureHeader = "X-Autopilot-Signature";
    public const string TimestampHeader = "X-Autopilot-Timestamp";
    public const string SourceHeader = "X-Autopilot-Source";
    public const string EventIdHeader = "X-Autopilot-Event-Id";
    public const string EventTypeHeader = "X-Autopilot-Event-Type";

    [HttpPost("events")]
    [EnableRateLimiting(RateLimiting.Generation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Submit(CancellationToken cancellationToken)
    {
        string payload;
        using (var reader = new StreamReader(Request.Body))
        {
            payload = await reader.ReadToEndAsync(cancellationToken);
        }

        var result = await intake.SubmitAsync(new AutopilotIntakeRequest(
            Request.Headers[SignatureHeader].ToString(),
            Request.Headers[TimestampHeader].ToString(),
            payload,
            Request.Headers[SourceHeader].ToString(),
            Request.Headers[EventIdHeader].ToString(),
            Request.Headers[EventTypeHeader].ToString(),
            HttpContext.TraceIdentifier), cancellationToken);

        return result.Outcome switch
        {
            AutopilotIntakeOutcomes.Accepted => Accepted(new { status = result.Outcome, eventId = result.EventId }),
            AutopilotIntakeOutcomes.Duplicate => Ok(new { status = result.Outcome, eventId = result.EventId, reason = result.Reason }),
            AutopilotIntakeOutcomes.RejectedUnsigned => Unauthorized(new { error = new { code = "AUTOPILOT_EVENT_UNAUTHENTICATED", message = "The event could not be authenticated." } }),
            _ => ApiResults.Error(this, StatusCodes.Status400BadRequest, "AUTOPILOT_EVENT_REJECTED", "The event was rejected."),
        };
    }
}
