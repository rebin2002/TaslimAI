using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Operations;
using Taslim.Api.Persistence;

namespace Taslim.Api.Infrastructure;

public static class OperationalObservabilityHeaders
{
    public const string RequestId = "X-Request-ID";
}

public sealed class RequestCorrelationMiddleware(
    RequestDelegate next,
    ILogger<RequestCorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Request IDs are emitted into operational logs and are therefore a
        // server-owned audit boundary. Do not echo a caller-supplied value:
        // otherwise a customer could impersonate another request's ID and
        // make incident traces ambiguous even though the value is sanitized.
        var requestId = CreateRequestId();
        context.TraceIdentifier = requestId;
        context.Response.Headers[OperationalObservabilityHeaders.RequestId] = requestId;
        // Boundaries below this middleware can reset the response before
        // writing their own safe failure envelope: the exception handler
        // clears headers and rewrites the status on unhandled failures,
        // which drops the correlation ID exactly when a client needs an
        // error ID to quote. Re-assert the server-owned value at the last
        // moment before any response starts so every outcome, 2xx or 5xx,
        // stays traceable to the RequestId logged for the request.
        context.Response.OnStarting(static state =>
        {
            var (response, value) = ((HttpResponse, string))state;
            response.Headers[OperationalObservabilityHeaders.RequestId] = value;
            return Task.CompletedTask;
        }, (context.Response, requestId));

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = requestId,
            ["HttpMethod"] = context.Request.Method,
            ["RequestPath"] = context.Request.Path.Value ?? "/",
        }))
        {
            await next(context);
        }
    }

    private static string CreateRequestId() => Guid.NewGuid().ToString("N");
}

public sealed class HealthOptions
{
    public bool StorageRequired { get; set; } = true;
    public int ProbeTimeoutSeconds { get; set; } = 5;

    public TimeSpan ProbeTimeout => TimeSpan.FromSeconds(Math.Clamp(ProbeTimeoutSeconds, 1, 60));
}

public sealed record OperationalHealthCheckDto(string Name, string Status, bool Required);
public sealed record OperationalHealthResponse(
    string Status,
    string Service,
    string RequestId,
    IReadOnlyList<OperationalHealthCheckDto> Checks);

public sealed class OperationalHealthService(
    TaslimDbContext db,
    IFileStorageService storage,
    IOptions<HealthOptions> healthOptions,
    ILogger<OperationalHealthService> logger,
    IHostApplicationLifetime? applicationLifetime = null,
    IOptions<GenerationJobOptions>? generationOptions = null)
{
    private readonly HealthOptions settings = healthOptions.Value;
    private readonly IHostApplicationLifetime? lifetime = applicationLifetime;

    // The worker probe shares the options that the worker itself uses to write
    // heartbeats, so readiness cannot drift away from the administrator
    // operations report. When the options are absent (for example a hand-built
    // service in a focused test) the probe assumes a worker IS expected, which
    // reports degradation rather than silently skipping the check.
    private readonly GenerationJobOptions generationSettings = generationOptions?.Value ?? new GenerationJobOptions();

    public OperationalHealthResponse Live(string requestId) =>
        new("alive", "Taslim API", requestId, [new("process", "alive", true)]);

    public async Task<(OperationalHealthResponse Response, bool IsReady)> ReadinessAsync(string requestId, CancellationToken cancellationToken)
    {
        if (lifetime?.ApplicationStopping.IsCancellationRequested == true
            || lifetime?.ApplicationStopped.IsCancellationRequested == true)
        {
            logger.LogInformation("Readiness rejected because host shutdown is in progress. RequestId={RequestId}", requestId);
            return (new("not_ready", "Taslim API", requestId, [new("application", "stopping", true)]), false);
        }

        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(settings.ProbeTimeout);

        var checks = new List<OperationalHealthCheckDto>();
        var databaseStatus = "available";
        try
        {
            databaseStatus = await db.Database.CanConnectAsync(probeTimeout.Token).WaitAsync(probeTimeout.Token) ? "available" : "unavailable";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            databaseStatus = "unavailable";
            logger.LogWarning("Readiness database check timed out. RequestId={RequestId}", requestId);
        }
        catch (Exception exception)
        {
            databaseStatus = "unavailable";
            logger.LogWarning(exception, "Readiness database check failed. RequestId={RequestId}", requestId);
        }
        checks.Add(new("database", databaseStatus, true));

        var storageStatus = probeTimeout.IsCancellationRequested
            ? "unavailable"
            : await CheckStorageAsync(probeTimeout.Token, cancellationToken, requestId);
        checks.Add(new("storage", storageStatus, settings.StorageRequired));

        // Worker degradation is reported but never gates readiness. A replica can
        // serve HTTP correctly while no generation worker drains the queue, so
        // failing readiness here would pull healthy API instances out of rotation
        // and, in a single-container deployment, restart-loop the API during a
        // provider outage that only stalls jobs. Before this check existed the
        // document could only ever say "ready", which hid the stalled queue from
        // the incident procedure that records the readiness checks.
        //
        // `unknown` is deliberately distinct from `degraded`: a probe that could
        // not run or could not complete must not be reported as evidence about
        // the worker pool.
        var workerStatus = probeTimeout.IsCancellationRequested
            ? "unknown"
            : await CheckWorkerAsync(probeTimeout.Token, cancellationToken, requestId);
        checks.Add(new("worker", workerStatus, false));

        var ready = databaseStatus == "available"
            && (!settings.StorageRequired || storageStatus == "available");
        return (new(ready ? "ready" : "not_ready", "Taslim API", requestId, checks), ready);
    }

    private async Task<string> CheckStorageAsync(CancellationToken probeCancellationToken, CancellationToken requestCancellationToken, string requestId)
    {
        try
        {
            // A read-only existence check verifies the configured adapter without
            // writing a sentinel object or exposing a storage key.
            await storage.ExistsAsync("_health/readiness", probeCancellationToken).WaitAsync(probeCancellationToken);
            return "available";
        }
        catch (FileStorageUnavailableException)
        {
            return "unconfigured";
        }
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Readiness storage check timed out. RequestId={RequestId}; StorageProvider={StorageProvider}", requestId, storage.ProviderKey);
            return "unavailable";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Readiness storage check failed. RequestId={RequestId}; StorageProvider={StorageProvider}", requestId, storage.ProviderKey);
            return "unavailable";
        }
    }

    private async Task<string> CheckWorkerAsync(CancellationToken probeCancellationToken, CancellationToken requestCancellationToken, string requestId)
    {
        if (!generationSettings.WorkerEnabled)
        {
            // This deployment runs no generation worker (a Testing host or a
            // web-only replica), so the absence of heartbeats is not degradation.
            return "disabled";
        }

        try
        {
            var staleAfter = GenerationWorkerLiveness.StaleWindow(generationSettings.HeartbeatStaleAfterSeconds);
            var cutoff = DateTime.UtcNow - staleAfter;
            // The worker touches its heartbeat on every loop iteration, including
            // an idle poll with an empty queue, so a fresh running heartbeat is
            // positive evidence that jobs will be claimed. Heartbeats are
            // fleet-wide, which is why this probe answers "is any worker alive"
            // rather than judging this replica's own slots.
            var hasLiveWorker = await db.GenerationWorkerHeartbeats.AsNoTracking()
                .Where(item => item.Status == GenerationWorkerStatuses.Running && item.LastSeenAt > cutoff)
                .AnyAsync(probeCancellationToken)
                .WaitAsync(probeCancellationToken);
            return hasLiveWorker ? "available" : "degraded";
        }
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Readiness worker check timed out. RequestId={RequestId}", requestId);
            return "unknown";
        }
        catch (Exception exception)
        {
            // A failed query is an infrastructure fault, not proof that the
            // worker pool is dead. Reporting it as degradation would misdirect
            // an incident, so the evidence stays explicitly unknown.
            logger.LogWarning(exception, "Readiness worker check failed. RequestId={RequestId}", requestId);
            return "unknown";
        }
    }
}
