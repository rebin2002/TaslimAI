using System.Text;
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
public sealed class AutopilotIntakeController(IAutopilotEventIntake intake, AutopilotOptions options) : ControllerBase
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
    [ProducesResponseType(StatusCodes.Status413RequestEntityTooLarge)]
    public async Task<IActionResult> Submit(CancellationToken cancellationToken)
    {
        var payloadResult = await ReadPayloadAsync(Request.Body, options.MaxEventPayloadCharacters, cancellationToken);
        if (payloadResult.TooLarge)
            return ApiResults.Error(this, StatusCodes.Status413RequestEntityTooLarge, "AUTOPILOT_EVENT_TOO_LARGE", "The event payload is too large.");

        var result = await intake.SubmitAsync(new AutopilotIntakeRequest(
            Request.Headers[SignatureHeader].ToString(),
            Request.Headers[TimestampHeader].ToString(),
            payloadResult.Payload,
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

    private static async Task<(string Payload, bool TooLarge)> ReadPayloadAsync(Stream body, int configuredMaximumCharacters, CancellationToken cancellationToken)
    {
        var maximumCharacters = Math.Clamp(configuredMaximumCharacters, 256, 1_000_000);
        var buffer = new char[Math.Min(4096, maximumCharacters + 1)];
        var payload = new StringBuilder(Math.Min(maximumCharacters, 4096));

        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return (payload.ToString(), false);
            if (payload.Length > maximumCharacters - read) return (string.Empty, true);
            payload.Append(buffer, 0, read);
        }
    }
}
