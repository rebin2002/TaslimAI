using Microsoft.EntityFrameworkCore;
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
    ILogger<OperationalHealthService> logger)
{
    private readonly HealthOptions settings = healthOptions.Value;

    public OperationalHealthResponse Live(string requestId) =>
        new("alive", "Taslim API", requestId, [new("process", "alive", true)]);

    public async Task<(OperationalHealthResponse Response, bool IsReady)> ReadinessAsync(string requestId, CancellationToken cancellationToken)
    {
        var checks = new List<OperationalHealthCheckDto>();
        var databaseStatus = "available";
        try
        {
            databaseStatus = await db.Database.CanConnectAsync(cancellationToken) ? "available" : "unavailable";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            databaseStatus = "unavailable";
            logger.LogWarning(exception, "Readiness database check failed. RequestId={RequestId}", requestId);
        }
        checks.Add(new("database", databaseStatus, true));

        var storageStatus = await CheckStorageAsync(cancellationToken);
        checks.Add(new("storage", storageStatus, settings.StorageRequired));

        var ready = databaseStatus == "available"
            && (!settings.StorageRequired || storageStatus == "available");
        return (new(ready ? "ready" : "not_ready", "Taslim API", requestId, checks), ready);
    }

    private async Task<string> CheckStorageAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A read-only existence check verifies the configured adapter without
            // writing a sentinel object or exposing a storage key.
            await storage.ExistsAsync("_health/readiness", cancellationToken);
            return "available";
        }
        catch (FileStorageUnavailableException)
        {
            return "unconfigured";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Readiness storage check failed. StorageProvider={StorageProvider}", storage.ProviderKey);
            return "unavailable";
        }
    }
}
