using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
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
        var requestId = GetOrCreateRequestId(context.Request.Headers[OperationalObservabilityHeaders.RequestId].FirstOrDefault());
        context.TraceIdentifier = requestId;
        context.Response.Headers[OperationalObservabilityHeaders.RequestId] = requestId;

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

    private static string GetOrCreateRequestId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 128) return Guid.NewGuid().ToString("N");
        return candidate.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            ? candidate
            : Guid.NewGuid().ToString("N");
    }
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
    IHostApplicationLifetime? applicationLifetime = null)
{
    private readonly HealthOptions settings = healthOptions.Value;
    private readonly IHostApplicationLifetime? lifetime = applicationLifetime;

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
}
