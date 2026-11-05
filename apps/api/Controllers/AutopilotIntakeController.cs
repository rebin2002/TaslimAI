using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Autopilot;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

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
public sealed class AutopilotIntakeController(
    IAutopilotEventIntake intake,
    TaslimDbContext db,
    IAutopilotEventAuthenticator authenticator,
    AutopilotOptions options,
    IAutopilotLockService locks,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<AutopilotIntakeController> logger,
    Action? notifyCommittedEvent = null) : ControllerBase
{
    private readonly Action eventNotifier = notifyCommittedEvent ?? AutopilotEventDispatchSignal.NotifyCommittedEvent;
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
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Submit(CancellationToken cancellationToken)
    {
        var payloadResult = await ReadPayloadAsync(Request.Body, options.MaxEventPayloadCharacters, cancellationToken);
        if (payloadResult.TooLarge)
            return ApiResults.Error(this, StatusCodes.Status413RequestEntityTooLarge, "AUTOPILOT_EVENT_TOO_LARGE", "The event payload is too large.");

        var source = Request.Headers[SourceHeader].ToString();
        var externalEventId = Request.Headers[EventIdHeader].ToString();
        var eventType = Request.Headers[EventTypeHeader].ToString();

        if (string.IsNullOrWhiteSpace(source.Trim())
            || string.IsNullOrWhiteSpace(externalEventId.Trim())
            || !AutopilotEventTypes.Supported.Contains(eventType.Trim()))
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "AUTOPILOT_EVENT_REJECTED", "The event was rejected.");

        AutopilotSignatureResult verification;
        try
        {
            // Reuse the intake's common HMAC/timestamp verifier before acquiring
            // the shared launch fence. SubmitAsync verifies again while holding
            // the fence before persistence, so this is only an admission preflight.
            verification = authenticator.Verify(
                Request.Headers[SignatureHeader].ToString(),
                Request.Headers[TimestampHeader].ToString(),
                payloadResult.Payload,
                DateTime.UtcNow);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "AUTOPILOT_EVENT_REJECTED", "The event was rejected.");
        }

        if (!verification.Valid)
        {
            if (string.Equals(verification.FailureReason, "timestamp_out_of_tolerance", StringComparison.Ordinal))
                return ApiResults.Error(this, StatusCodes.Status400BadRequest, "AUTOPILOT_EVENT_REJECTED", "The event was rejected.");

            return Unauthorized(new { error = new { code = "AUTOPILOT_EVENT_UNAUTHENTICATED", message = "The event could not be authenticated." } });
        }

        if (!HasValidManusBridgeReceiptEventIdBinding(payloadResult.Payload, source, externalEventId, eventType))
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "AUTOPILOT_EVENT_REJECTED", "The event was rejected.");

        AutopilotLockHandle fence;
        try
        {
            fence = await locks.AcquireAsync(
                AutopilotCompletionLaunchFence.ResourceKey,
                "autopilot-event-intake",
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "Autopilot intake fence acquisition failed. EventId={EventId}; ExceptionType={ExceptionType}",
                externalEventId, exception.GetType().Name);
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable,
                "AUTOPILOT_EVENT_RETRY", "The event was not accepted; retry the same event ID and payload.");
        }

        if (!fence.IsAcquired)
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable,
                "AUTOPILOT_EVENT_RETRY", "The event was not accepted; retry the same event ID and payload.");

        AsyncServiceScope renewalScope;
        try
        {
            renewalScope = serviceScopeFactory.CreateAsyncScope();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Autopilot intake could not create an isolated renewal scope. EventId={EventId}", externalEventId);
            try { _ = await locks.ReleaseAsync(fence, CancellationToken.None); }
            catch (Exception releaseException)
            {
                logger.LogError(releaseException, "Autopilot intake could not release a fence after scope creation failure. EventId={EventId}", externalEventId);
            }
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable,
                "AUTOPILOT_EVENT_RETRY", "The event was not accepted; retry the same event ID and payload.");
        }
        await using var renewalScopeLifetime = renewalScope;

        IAutopilotLockService renewalLocks;
        try
        {
            renewalLocks = renewalScopeLifetime.ServiceProvider.GetRequiredService<IAutopilotLockService>();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Autopilot intake could not resolve an isolated renewal lock service. EventId={EventId}", externalEventId);
            try { _ = await locks.ReleaseAsync(fence, CancellationToken.None); }
            catch (Exception releaseException)
            {
                logger.LogError(releaseException, "Autopilot intake could not release a fence after renewal service resolution failure. EventId={EventId}", externalEventId);
            }
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable,
                "AUTOPILOT_EVENT_RETRY", "The event was not accepted; retry the same event ID and payload.");
        }

        var fenceHeartbeat = await AutopilotLockLeaseHeartbeat.StartAsync(
            renewalLocks,
            fence,
            AutopilotCompletionLaunchFence.RenewalInterval(options.LockLeaseMinutes),
            logger,
            CancellationToken.None);
        if (fenceHeartbeat is null)
        {
            try { _ = await renewalLocks.ReleaseAsync(fence, CancellationToken.None); }
            catch (Exception exception)
            {
                logger.LogError(exception, "Autopilot intake could not release a fence after initial renewal failure. EventId={EventId}", externalEventId);
            }
            logger.LogError("Autopilot intake fence could not be renewed before persistence. EventId={EventId}", externalEventId);
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable,
                "AUTOPILOT_EVENT_RETRY", "The event was not accepted; retry the same event ID and payload.");
        }
        await using var fenceHeartbeatScope = fenceHeartbeat;

        AutopilotIntakeResult? result = null;
        var fenceReleased = false;
        try
        {
            // Stop the child-scope heartbeat before opening the persistence
            // transaction. Renew the exact owner/token using this request's
            // DbContext inside the transaction; that UPDATE row-locks the fence
            // until SubmitAsync and all its audit writes commit or roll back.
            await fenceHeartbeatScope.StopRenewalLoopAsync();
            if (!fenceHeartbeatScope.IsLost
                && locks is IAutopilotLockLeaseRenewalService requestScopeRenewal)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var transactionFence = await requestScopeRenewal.RenewAsync(fence, cancellationToken);
                if (transactionFence.IsAcquired
                    && transactionFence.FencingToken == fence.FencingToken
                    && string.Equals(transactionFence.OwnerToken, fence.OwnerToken, StringComparison.Ordinal))
                {
                    result = await intake.SubmitAsync(new AutopilotIntakeRequest(
                        Request.Headers[SignatureHeader].ToString(),
                        Request.Headers[TimestampHeader].ToString(),
                        payloadResult.Payload,
                        source,
                        externalEventId,
                        eventType,
                        HttpContext.TraceIdentifier), cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    logger.LogError("Autopilot intake fence token was not live inside the persistence transaction. EventId={EventId}", externalEventId);
                }
            }
            else
            {
                logger.LogError("Autopilot intake lost its fence before opening the persistence transaction. EventId={EventId}", externalEventId);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Autopilot intake transaction failed before durable acknowledgement. EventId={EventId}", externalEventId);
            result = null;
        }
        finally
        {
            try
            {
                await fenceHeartbeatScope.StopRenewalLoopAsync();
                if (!fenceHeartbeatScope.IsLost
                    && await fenceHeartbeatScope.RenewNowAsync(CancellationToken.None))
                    fenceReleased = await renewalLocks.ReleaseAsync(fenceHeartbeatScope.CurrentHandle, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Autopilot intake fence renewal/release failed. EventId={EventId}", externalEventId);
            }
        }

        if (!fenceReleased || fenceHeartbeatScope.IsLost || result is null)
        {
            logger.LogError("Autopilot intake lost its completion/launch fence before acknowledgement. EventId={EventId}", externalEventId);
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable,
                "AUTOPILOT_EVENT_RETRY", "The event acknowledgement is uncertain; retry the same event ID and payload.");
        }

        if (result.Outcome is AutopilotIntakeOutcomes.Accepted or AutopilotIntakeOutcomes.Duplicate)
            eventNotifier();

        return result.Outcome switch
        {
            AutopilotIntakeOutcomes.Accepted => Accepted(new { status = result.Outcome, eventId = result.EventId, externalEventId = externalEventId.Trim() }),
            AutopilotIntakeOutcomes.Duplicate => Ok(new { status = result.Outcome, eventId = result.EventId, externalEventId = externalEventId.Trim(), reason = result.Reason }),
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
