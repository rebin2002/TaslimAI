using System.Text;
using System.Text.Json;
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

        var source = Request.Headers[SourceHeader].ToString();
        var externalEventId = Request.Headers[EventIdHeader].ToString();
        var eventType = Request.Headers[EventTypeHeader].ToString();
        if (!HasValidManusBridgeReceiptEventIdBinding(payloadResult.Payload, source, externalEventId, eventType))
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "AUTOPILOT_EVENT_REJECTED", "The event was rejected.");

        var result = await intake.SubmitAsync(new AutopilotIntakeRequest(
            Request.Headers[SignatureHeader].ToString(),
            Request.Headers[TimestampHeader].ToString(),
            payloadResult.Payload,
            source,
            externalEventId,
            eventType,
            HttpContext.TraceIdentifier), cancellationToken);

        if (result.Outcome is AutopilotIntakeOutcomes.Accepted or AutopilotIntakeOutcomes.Duplicate)
            AutopilotEventDispatchSignal.NotifyCommittedEvent();

        return result.Outcome switch
        {
            AutopilotIntakeOutcomes.Accepted => Accepted(new { status = result.Outcome, eventId = result.EventId }),
            AutopilotIntakeOutcomes.Duplicate => Ok(new { status = result.Outcome, eventId = result.EventId, reason = result.Reason }),
            AutopilotIntakeOutcomes.RejectedUnsigned => Unauthorized(new { error = new { code = "AUTOPILOT_EVENT_UNAUTHENTICATED", message = "The event could not be authenticated." } }),
            _ => ApiResults.Error(this, StatusCodes.Status400BadRequest, "AUTOPILOT_EVENT_REJECTED", "The event was rejected."),
        };
    }

    private static bool HasValidManusBridgeReceiptEventIdBinding(
        string payload,
        string source,
        string externalEventId,
        string eventType)
    {
        if (!string.Equals(source.Trim(), ManusBridgeWaveLaunchProvider.Key, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(eventType.Trim(), AutopilotEventTypes.WaveTaskCompleted, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var payloadDocument = JsonDocument.Parse(payload);
            var root = payloadDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetSingleStringProperty(root, "outcome", out var outcome))
                return false;

            var isSucceeded = string.Equals(outcome.Trim(), AutopilotTaskOutcomes.Succeeded, StringComparison.OrdinalIgnoreCase);
            var evidenceProperties = root.EnumerateObject().Where(property => property.NameEquals("evidence")).ToArray();
            if (evidenceProperties.Length != 1 || evidenceProperties[0].Value.ValueKind != JsonValueKind.String)
                return !isSucceeded && evidenceProperties.Length == 0;

            var evidenceJson = evidenceProperties[0].Value.GetString();
            if (string.IsNullOrWhiteSpace(evidenceJson)) return !isSucceeded;

            JsonDocument evidenceDocument;
            try
            {
                evidenceDocument = JsonDocument.Parse(evidenceJson);
            }
            catch (JsonException)
            {
                return !isSucceeded;
            }

            using (evidenceDocument)
            {
                var evidence = evidenceDocument.RootElement;
                if (evidence.ValueKind != JsonValueKind.Object)
                    return !isSucceeded;

                var hasReceiptKind = TryGetSingleStringProperty(evidence, "kind", out var kind)
                    && string.Equals(kind, AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind, StringComparison.Ordinal);
                if (isSucceeded && !hasReceiptKind) return false;

                var hasEventId = evidence.EnumerateObject().Any(property => property.NameEquals("event_id"));
                if (!hasReceiptKind && !hasEventId) return true;

                var normalizedExternalEventId = externalEventId.Trim();
                return TryGetSingleStringProperty(evidence, "event_id", out var receiptEventId)
                    && normalizedExternalEventId.Length > 0
                    && string.Equals(receiptEventId, normalizedExternalEventId, StringComparison.Ordinal);
            }
        }
        catch (JsonException)
        {
            // A Bridge success without a parseable, bound terminal receipt is
            // never allowed to reach durable intake or advance orchestration.
            return false;
        }
    }

    private static bool TryGetSingleStringProperty(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            count++;
            if (property.Value.ValueKind != JsonValueKind.String) return false;
            value = property.Value.GetString() ?? string.Empty;
        }

        return count == 1;
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
