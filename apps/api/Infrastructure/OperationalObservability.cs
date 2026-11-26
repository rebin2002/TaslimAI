using System.Diagnostics;
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

    // The readiness route is anonymous and the worker verdict is advisory, so an
    // external monitor fleet (or a retry storm against a 503) can ask the same
    // question far faster than the worker pool can change its answer. This bounds
    // how many database round-trips per replica that advice is worth; 0 disables
    // the bound and restores one heartbeat read per probe.
    public int WorkerProbeCacheSeconds { get; set; } = 2;

    public TimeSpan WorkerProbeCache => TimeSpan.FromSeconds(Math.Clamp(WorkerProbeCacheSeconds, 0, 60));
}

/// <summary>
/// Single-flight, short-lived memo for the advisory worker-liveness verdict,
/// shared by every readiness probe on one replica. It exists to bound the
/// database cost that <see cref="OperationalHealthService"/> adds to an
/// unauthenticated route: the readiness endpoint carries no rate-limit policy
/// (main's <c>RateLimiting.cs</c> has no health policy, and adding one is the
/// separate scope of PR #249), so the concurrency of that read is otherwise
/// whatever the caller can afford to send.
/// </summary>
/// <remarks>
/// Only verdicts that are actual evidence about the worker pool are cached.
/// <c>unknown</c> is the absence of evidence, and a stale verdict is never held
/// past the window: an operator reading "degraded" while a worker is recovering
/// must see recovery within a couple of seconds, not within a heartbeat window.
/// The cached value is a bare status string, so this singleton never captures a
/// scoped <see cref="Taslim.Api.Persistence.TaslimDbContext"/> or storage adapter.
/// </remarks>
public sealed class WorkerLivenessProbeCache(TimeSpan ttl, Func<DateTimeOffset>? nowProvider = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<DateTimeOffset> now = nowProvider ?? (() => DateTimeOffset.UtcNow);
    private string? verdict;
    private long validUntilUtcTicks;
    private long lastStampedUtcTicks;
    private long probeExecutions;

    /// <summary>How many times this cache actually asked its source.</summary>
    public long ProbeExecutions => Interlocked.Read(ref probeExecutions);

    public TimeSpan Ttl => ttl;

    public static bool IsWorkerPoolEvidence(string status) =>
        status is "available" or "degraded" or "disabled";

    /// <param name="probe">Runs the liveness query; receives a budget-only token.</param>
    /// <param name="probeBudgetToken">
    /// Timeout token for the shared query. It is deliberately not the caller's
    /// request token: a client that disconnects mid-query must not cancel the
    /// evidence that every waiting probe is about to reuse.
    /// </param>
    /// <param name="callerToken">This caller's own cancellation, so waiting is still responsive.</param>
    /// <param name="waitBudget">
    /// How long this caller may queue behind a query already in flight. Waiting is
    /// bounded because the readiness route is anonymous: an unbounded queue would
    /// hold a request, a DI scope and a pooled connection for one query per probe
    /// timeout, which is a worse outage than the parallel reads it replaces. A
    /// caller whose budget runs out is released with advice, never with a hang.
    /// </param>
    public async Task<string> ObserveAsync(
        Func<CancellationToken, Task<string>> probe,
        CancellationToken probeBudgetToken,
        CancellationToken callerToken,
        TimeSpan waitBudget)
    {
        if (ttl <= TimeSpan.Zero)
        {
            // Bounding disabled: the pre-fix behaviour, one query per probe.
            Interlocked.Increment(ref probeExecutions);
            return await probe(probeBudgetToken);
        }

        if (waitBudget <= TimeSpan.Zero)
        {
            // No budget left to queue with, so answer from advice or from nothing.
            return ReadWindow() ?? "unknown";
        }

        if (!await gate.WaitAsync(waitBudget, callerToken))
        {
            // Another probe is still running. Release this caller with the freshest
            // advice available instead of making an anonymous request wait it out.
            return ReadWindow() ?? "unknown";
        }

        try
        {
            var cached = ReadWindow();
            if (cached is not null)
            {
                return cached;
            }

            Interlocked.Increment(ref probeExecutions);
            var status = await probe(probeBudgetToken);
            if (IsWorkerPoolEvidence(status))
            {
                // The window is stamped from the moment the evidence exists, not
                // from the moment the query started. Stamping at the start would
                // write an already-expired window whenever the read is slower than
                // the window itself - which is precisely the slow-database state
                // this bound exists for, and it would leave a serialising mutex and
                // no saving at all.
                var current = now();
                var currentTicks = current.UtcTicks;
                verdict = status;
                Interlocked.Exchange(ref lastStampedUtcTicks, currentTicks);
                Interlocked.Exchange(ref validUntilUtcTicks, currentTicks + ttl.Ticks);
            }
            else
            {
                // Never serve the absence of evidence.
                verdict = null;
                Interlocked.Exchange(ref validUntilUtcTicks, 0);
                Interlocked.Exchange(ref lastStampedUtcTicks, 0);
            }
            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The memoised verdict while it is inside its window, otherwise null. A clock
    /// that has stepped backwards (an NTP correction, a restored snapshot) is
    /// treated as no window at all, so a jump can never stretch advice past the
    /// freshness the operator configured.
    /// </summary>
    private string? ReadWindow()
    {
        var cached = Volatile.Read(ref verdict);
        var until = Interlocked.Read(ref validUntilUtcTicks);
        if (cached is null || until == 0)
        {
            return null;
        }

        var currentTicks = now().UtcTicks;
        return currentTicks >= Interlocked.Read(ref lastStampedUtcTicks) && currentTicks < until
            ? cached
            : null;
    }
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
    IOptions<GenerationJobOptions>? generationOptions = null,
    WorkerLivenessProbeCache? workerProbeCache = null)
{
    private readonly HealthOptions settings = healthOptions.Value;
    private readonly IHostApplicationLifetime? lifetime = applicationLifetime;
    // Supplied by production DI as a replica-wide singleton so consecutive
    // probes share one bounded read. When it is absent — a hand-built service in
    // a focused test, or a host that never registered it — the probe runs
    // uncached, exactly as before: no existing expectation about freshness or
    // per-call database work changes, and no test inherits another test's
    // process-wide verdict.
    private readonly WorkerLivenessProbeCache? workerProbe = workerProbeCache;

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
        // Monotonic, so a budget is never mis-measured by a host clock correction.
        var probeStopwatch = Stopwatch.StartNew();

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
        // What is left of the configured probe timeout, after the database and
        // storage checks have had their turn, is the whole budget the worker stage
        // may spend - shared read included. Handing the shared read a fresh full
        // timeout instead would let one readiness document take twice the bound
        // the operations runbook advertises, and would exceed the smoke script's
        // own request deadline on a slow-storage incident.
        var remainingProbeBudget = settings.ProbeTimeout - probeStopwatch.Elapsed;
        var workerStatus = probeTimeout.IsCancellationRequested || remainingProbeBudget <= TimeSpan.Zero
            ? "unknown"
            : await CheckWorkerAsync(remainingProbeBudget, cancellationToken, requestId);
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
            // Readiness is anonymous. Do not disclose whether deployment storage
            // configuration is absent; expose only the safe dependency state.
            return "unavailable";
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

    private async Task<string> CheckWorkerAsync(TimeSpan remainingBudget, CancellationToken requestCancellationToken, string requestId)
    {
        if (!generationSettings.WorkerEnabled)
        {
            // This deployment runs no generation worker (a Testing host or a
            // web-only replica), so the absence of heartbeats is not degradation.
            return "disabled";
        }

        using var probeBudget = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
        probeBudget.CancelAfter(remainingBudget);

        if (workerProbe is null)
        {
            // No replica-wide cache was supplied, so every probe reads through,
            // inside the caller's own linked budget, exactly as before.
            return await ProbeWorkerHeartbeatsAsync(probeBudget.Token, requestCancellationToken, requestId);
        }

        // The shared read gets its own budget instead of the caller's linked
        // token: a monitor that hangs up mid-query must not cancel the verdict
        // that the probes queued behind it are about to reuse, and an anonymous
        // disconnect storm must not become a query relaunch storm. That budget is
        // the caller's remaining budget, and the queue wait is bounded by it too,
        // so neither the read nor the request can outlive the documented probe
        // timeout.
        using var sharedBudget = new CancellationTokenSource(remainingBudget);
        return await workerProbe.ObserveAsync(
            token => ProbeWorkerHeartbeatsAsync(token, requestCancellationToken, requestId),
            sharedBudget.Token,
            requestCancellationToken,
            remainingBudget);
    }

    private async Task<string> ProbeWorkerHeartbeatsAsync(CancellationToken probeCancellationToken, CancellationToken requestCancellationToken, string requestId)
    {
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
