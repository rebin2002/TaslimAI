using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

public sealed record AutopilotIntakeRequest(
    string? SignatureHeader,
    string? TimestampHeader,
    string PayloadJson,
    string SourceSystem,
    string ExternalEventId,
    string EventType,
    string? RequestId = null);

public sealed record AutopilotIntakeResult(string Outcome, Guid? EventId, string? Reason);

public static class AutopilotIntakeOutcomes
{
    public const string Accepted = "accepted";
    public const string Duplicate = "duplicate";
    public const string RejectedUnsigned = "rejected_unsigned";
    public const string RejectedReplay = "rejected_replay";
    public const string RejectedInvalid = "rejected_invalid";
    public const string PayloadConflict = "payload_conflict";
    public const string ShaConflict = "sha_conflict";
}

/// <summary>
/// Durable completion queue intake. Verifies authenticity, rejects replays and
/// duplicate deliveries, enforces immutable SHAs, and persists the event before
/// any orchestration happens.
/// </summary>
public interface IAutopilotEventIntake
{
    Task<AutopilotIntakeResult> SubmitAsync(AutopilotIntakeRequest request, CancellationToken cancellationToken = default);
}

public sealed class AutopilotEventIntake(
    TaslimDbContext db,
    IAutopilotEventAuthenticator authenticator,
    IAutopilotAuditLog audit,
    AutopilotOptions options,
    TimeProvider timeProvider) : IAutopilotEventIntake
{
    public async Task<AutopilotIntakeResult> SubmitAsync(AutopilotIntakeRequest request, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var source = (request.SourceSystem ?? string.Empty).Trim();
        var externalId = (request.ExternalEventId ?? string.Empty).Trim();
        var eventType = (request.EventType ?? string.Empty).Trim();

        if (source.Length == 0 || externalId.Length == 0 || !AutopilotEventTypes.Supported.Contains(eventType))
            return await RejectAsync(request, AutopilotEventStatuses.RejectedInvalid, "event_identity_invalid", cancellationToken);

        if (request.PayloadJson.Length > Math.Max(256, options.MaxEventPayloadCharacters))
            return await RejectAsync(request, AutopilotEventStatuses.RejectedInvalid, "payload_too_large", cancellationToken);

        var verification = authenticator.Verify(request.SignatureHeader, request.TimestampHeader, request.PayloadJson, now);
        if (!verification.Valid)
        {
            // A stale timestamp is a replay, not an authentication failure.
            var isReplay = string.Equals(verification.FailureReason, "timestamp_out_of_tolerance", StringComparison.Ordinal);
            return await RejectAsync(
                request,
                isReplay ? AutopilotEventStatuses.RejectedReplay : AutopilotEventStatuses.RejectedUnsigned,
                verification.FailureReason ?? "signature_invalid",
                cancellationToken);
        }

        AutopilotCompletionSignal? signal;
        try
        {
            signal = JsonSerializer.Deserialize<AutopilotCompletionSignal>(request.PayloadJson);
        }
        catch (JsonException)
        {
            return await RejectAsync(request, AutopilotEventStatuses.RejectedInvalid, "payload_not_json", cancellationToken);
        }

        if (signal is null || string.IsNullOrWhiteSpace(signal.WaveKey) || string.IsNullOrWhiteSpace(signal.TaskId))
            return await RejectAsync(request, AutopilotEventStatuses.RejectedInvalid, "signal_incomplete", cancellationToken);

        var outcome = AutopilotTaskOutcomes.Supported.Contains(signal.Outcome ?? string.Empty)
            ? signal.Outcome!.Trim().ToLowerInvariant()
            : AutopilotTaskOutcomes.Succeeded;

        var payloadHash = authenticator.ComputePayloadHash(request.PayloadJson);
        var idempotencyKey = BuildIdempotencyKey(source, externalId);

        var existing = await db.AutopilotEvents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.SourceSystem == source && item.ExternalEventId == externalId, cancellationToken);

        if (existing is not null)
        {
            if (string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                // Replay of an identical delivery: acknowledged, never re-applied.
                await audit.RecordAsync(AutopilotAuditFactory.Create(
                    AutopilotAuditActions.EventDuplicateIgnored,
                    AutopilotAuditOutcomes.Skipped,
                    reason: "duplicate_delivery",
                    waveKey: signal.WaveKey,
                    taskId: signal.TaskId,
                    targetId: existing.Id,
                    requestId: request.RequestId,
                    dryRun: options.DryRun), cancellationToken);
                return new AutopilotIntakeResult(AutopilotIntakeOutcomes.Duplicate, existing.Id, "duplicate_delivery");
            }

            // Same identity, different content: refuse to reinterpret history.
            await audit.RecordAsync(AutopilotAuditFactory.Create(
                AutopilotAuditActions.EventRejected,
                AutopilotAuditOutcomes.Blocked,
                reason: "idempotency_key_payload_conflict",
                waveKey: signal.WaveKey,
                taskId: signal.TaskId,
                targetId: existing.Id,
                requestId: request.RequestId,
                dryRun: options.DryRun), cancellationToken);
            return new AutopilotIntakeResult(AutopilotIntakeOutcomes.PayloadConflict, existing.Id, "idempotency_key_payload_conflict");
        }

        var shaConflict = await DetectShaConflictAsync(signal, cancellationToken);
        if (shaConflict is not null)
        {
            var rejected = new AutopilotEvent
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                SourceSystem = source,
                ExternalEventId = externalId,
                IdempotencyKey = idempotencyKey,
                PayloadHash = payloadHash,
                Status = AutopilotEventStatuses.RejectedInvalid,
                SignatureVerified = true,
                ReplayProtected = true,
                Reason = shaConflict,
                WaveKey = signal.WaveKey,
                TaskId = signal.TaskId,
                Branch = signal.Branch,
                BaseSha = signal.BaseSha,
                CandidateSha = signal.CandidateSha,
                ReceivedAt = now,
                ProcessedAt = now,
                ConcurrencyToken = Guid.NewGuid(),
                SignalJson = request.PayloadJson,
            };
            db.AutopilotEvents.Add(rejected);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(AutopilotAuditFactory.Create(
                AutopilotAuditActions.SafetyStop,
                AutopilotAuditOutcomes.Blocked,
                reason: shaConflict,
                waveKey: signal.WaveKey,
                taskId: signal.TaskId,
                branch: signal.Branch,
                baseSha: signal.BaseSha,
                candidateSha: signal.CandidateSha,
                targetId: rejected.Id,
                requestId: request.RequestId,
                dryRun: options.DryRun), cancellationToken);
            return new AutopilotIntakeResult(AutopilotIntakeOutcomes.ShaConflict, rejected.Id, shaConflict);
        }

        var intakeEvent = new AutopilotEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            SourceSystem = source,
            ExternalEventId = externalId,
            IdempotencyKey = idempotencyKey,
            PayloadHash = payloadHash,
            Status = AutopilotEventStatuses.Queued,
            SignatureVerified = true,
            ReplayProtected = true,
            WaveKey = signal.WaveKey,
            TaskId = signal.TaskId,
            Branch = signal.Branch,
            BaseSha = signal.BaseSha,
            CandidateSha = signal.CandidateSha,
            Attempt = Math.Max(0, signal.Attempt ?? 0),
            ReceivedAt = now,
            QueuedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
            SignalJson = request.PayloadJson,
        };

        db.AutopilotEvents.Add(intakeEvent);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery won the race; the queue already holds one copy.
            db.Entry(intakeEvent).State = EntityState.Detached;
            var winner = await db.AutopilotEvents.AsNoTracking()
                .FirstOrDefaultAsync(item => item.SourceSystem == source && item.ExternalEventId == externalId, cancellationToken);
            if (winner is null) throw;
            return new AutopilotIntakeResult(AutopilotIntakeOutcomes.Duplicate, winner.Id, "concurrent_duplicate_delivery");
        }

        await audit.RecordAsync(AutopilotAuditFactory.Create(
            AutopilotAuditActions.EventReceived,
            AutopilotAuditOutcomes.Recorded,
            reason: outcome,
            statusDetail: "queued_for_orchestration",
            waveKey: signal.WaveKey,
            taskId: signal.TaskId,
            branch: signal.Branch,
            baseSha: signal.BaseSha,
            candidateSha: signal.CandidateSha,
            targetId: intakeEvent.Id,
            requestId: request.RequestId,
            dryRun: options.DryRun), cancellationToken);

        return new AutopilotIntakeResult(AutopilotIntakeOutcomes.Accepted, intakeEvent.Id, null);
    }

    public static string BuildIdempotencyKey(string sourceSystem, string externalEventId) =>
        $"autopilot:{sourceSystem.Trim().ToLowerInvariant()}:{externalEventId.Trim()}";

    private async Task<string?> DetectShaConflictAsync(AutopilotCompletionSignal signal, CancellationToken cancellationToken)
    {
        var run = await db.AutopilotRuns.AsNoTracking()
            .FirstOrDefaultAsync(item => item.WaveKey == signal.WaveKey, cancellationToken);
        if (run is null) return null;

        if (string.IsNullOrWhiteSpace(signal.BaseSha) || string.IsNullOrWhiteSpace(run.BaseSha))
        {
            return signal.BaseSha is not null && run.BaseSha.Length > 0 && !string.Equals(signal.BaseSha, run.BaseSha, StringComparison.Ordinal)
                ? "immutable_base_sha_conflict"
                : null;
        }

        if (!string.Equals(signal.BaseSha, run.BaseSha, StringComparison.Ordinal))
            return "immutable_base_sha_conflict";

        if (run.CandidateSha.Length > 0 && !string.IsNullOrWhiteSpace(signal.CandidateSha)
            && !string.Equals(signal.CandidateSha, run.CandidateSha, StringComparison.Ordinal))
            return "immutable_candidate_sha_conflict";

        return null;
    }

    private async Task<AutopilotIntakeResult> RejectAsync(
        AutopilotIntakeRequest request,
        string status,
        string reason,
        CancellationToken cancellationToken)
    {
        await audit.RecordAsync(AutopilotAuditFactory.Create(
            AutopilotAuditActions.EventRejected,
            AutopilotAuditOutcomes.Blocked,
            reason: reason,
            statusDetail: status,
            requestId: request.RequestId,
            dryRun: options.DryRun), cancellationToken);
        return new AutopilotIntakeResult(
            status switch
            {
                AutopilotEventStatuses.RejectedUnsigned => AutopilotIntakeOutcomes.RejectedUnsigned,
                AutopilotEventStatuses.RejectedReplay => AutopilotIntakeOutcomes.RejectedReplay,
                _ => AutopilotIntakeOutcomes.RejectedInvalid,
            },
            null,
            reason);
    }
}
