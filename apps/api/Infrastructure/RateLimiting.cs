using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Contracts;

namespace Taslim.Api.Infrastructure;

public static class RateLimiting
{
    public const string Authentication = "authentication";
    public const string Health = "health";
    public const string HealthLiveness = "health-live";
    public const string Chat = "chat-generation";
    public const string Generation = "generation-creation";
    public const string Upload = "uploads";
    public const string Search = "search";
    public const string ExpensiveAi = "expensive-ai";
    public const string AccountSecurity = "account-security";

    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsJsonAsync(
                new ErrorEnvelope(new ErrorBody("RATE_LIMITED", "Too many requests. Please try again later.")),
                cancellationToken);
        };

        // Endpoint policies below bound the number of requests over time. The
        // global limiter adds a no-queue concurrency bound to the same
        // provider-neutral user/IP partitions, so slow database, storage, or
        // provider work cannot accumulate behind an otherwise valid rate.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var policyName = httpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            var permitLimit = policyName switch
            {
                Authentication => 4,
                Health => 2,
                HealthLiveness => 4,
                Chat => 2,
                Generation => 4,
                Upload => 2,
                Search => 8,
                ExpensiveAi => 2,
                AccountSecurity => 1,
                _ => 0,
            };

            return permitLimit > 0
                ? RateLimitPartition.GetConcurrencyLimiter(
                    Partition($"concurrency:{policyName}", httpContext),
                    _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.NewestFirst,
                    })
                : RateLimitPartition.GetNoLimiter<string>("unlimited");
        });

        options.AddPolicy(Authentication, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            Partition("auth", httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        // Liveness and readiness deliberately do NOT share one bucket. At the
        // repository's documented smoke cadence (scripts/api-health-smoke.sh:
        // one liveness and one readiness probe every two seconds) a single
        // monitoring client behind one proxy peer source IP produces 60 health
        // requests per minute. Under the original combined 30/minute bucket the
        // 31st probe — a perfectly alive liveness check — was rejected as 429,
        // masking real process health. Liveness is a cheap in-memory response,
        // so it gets its own finite budget at twice the documented sustained
        // smoke rate: ordinary container plus orchestrator traffic stays
        // trustworthy, while anonymous flooding of the liveness aliases is
        // still bounded. Readiness keeps the expensive 30/minute,
        // 2-concurrency bound because each probe touches database and storage,
        // and the two lanes use separate concurrency partitions so slow
        // readiness calls cannot exhaust the liveness concurrency budget.
        // Partitions key on the connection RemoteIpAddress like every other
        // anonymous policy; arbitrary forwarded client IP headers are never
        // trusted, so same-peer monitors intentionally share a bucket and the
        // budgets must survive that.
        // A token bucket is used deliberately: .NET 8's fixed-window limiter
        // subdivides a budget into internal sub-windows, so the nominal
        // per-minute allowance is not the behavior an ordinary monitor sees —
        // the exact false-429 class this lane removes. Here the stated
        // sustained rate IS the real rate: one source can average 60 probes a
        // minute across both liveness aliases (double the documented smoke
        // cadence) with a bounded 60-request burst and no unbounded queueing.
        options.AddPolicy(HealthLiveness, httpContext => RateLimitPartition.GetTokenBucketLimiter(
            Partition("health-live", httpContext),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 60,
                TokensPerPeriod = 60,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        // Same segmentation caveat as above. The sustained allowance stays
        // exactly the documented 30 probes per minute — the expensive check is
        // not relaxed — but the bucket carries a 60-request ceiling so a
        // monitor's ordinary cadence is never disguised as overload. Abuse
        // still hits a finite bound at the same per-minute rate.
        options.AddPolicy(Health, httpContext => RateLimitPartition.GetTokenBucketLimiter(
            Partition("health", httpContext),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 60,
                TokensPerPeriod = 30,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        options.AddPolicy(Chat, httpContext => RateLimitPartition.GetTokenBucketLimiter(
            Partition("chat", httpContext),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 30,
                TokensPerPeriod = 30,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        options.AddPolicy(Generation, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            Partition("generation", httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        options.AddPolicy(Upload, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            Partition("upload", httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        options.AddPolicy(Search, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            Partition("search", httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        options.AddPolicy(ExpensiveAi, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            Partition("ai", httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

        options.AddPolicy(AccountSecurity, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            Partition("account-security", httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    private static string Partition(string prefix, HttpContext context, bool includePath = false)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var identity = string.IsNullOrWhiteSpace(userId)
            ? context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client"
            : $"user:{userId}";
        return includePath ? $"{prefix}:{identity}:{context.Request.Path}" : $"{prefix}:{identity}";
    }
}
