using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Contracts;

namespace Taslim.Api.Infrastructure;

public static class RateLimiting
{
    public const string Authentication = "authentication";
    public const string Chat = "chat-generation";
    public const string Generation = "generation-creation";
    public const string Upload = "uploads";
    public const string Search = "search";
    public const string ExpensiveAi = "expensive-ai";

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

        options.AddPolicy(Authentication, httpContext => RateLimitPartition.GetFixedWindowLimiter(
            Partition("auth", httpContext, includePath: true),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
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
