using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Contracts;

namespace Taslim.Api.Infrastructure;

public static class RateLimiting
{
    public const string Authentication = "authentication";
    public const string Registration = "registration";
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
                Registration => 2,
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

        options.AddPolicy(Registration, httpContext =>
        {
            // Registration provisions a workspace and billing ledger per
            // account, so it must not ride the login-tuned 60/minute window:
            // remote callers fail closed at a dedicated, far stricter budget.
            // Same-host traffic (local tooling, container health checks, the
            // CI browser suite that registers an account per test) can never
            // arrive over the network; it keeps its own generous but still
            // finite budget instead of an unbounded exemption. A missing peer
            // address only exists on transports with no network at all
            // (in-memory TestServer, unix sockets), never on a network
            // listener, so it shares the same-host budget too.
            var peerAddress = httpContext.Connection.RemoteIpAddress;
            var sameHost = peerAddress is null || IPAddress.IsLoopback(peerAddress);
            return RateLimitPartition.GetFixedWindowLimiter(
                Partition("registration", httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = sameHost ? 600 : 10,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        });

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
        var identity = PartitionIdentity(context);
        return includePath ? $"{prefix}:{identity}:{context.Request.Path}" : $"{prefix}:{identity}";
    }

    // Resolution of the caller a budget belongs to. Two properties have to hold at
    // once, and the obvious single-input implementations each destroy one of them:
    // a budget must follow one client (otherwise the limit is an exemption), and a
    // budget must not be shared by unrelated clients (otherwise a neighbour's
    // exhausted permits surface as this caller's 429, which the caller cannot
    // recover from). Every branch below therefore returns a stable, per-client key
    // that never carries the transport material it was derived from.
    private static string PartitionIdentity(HttpContext context)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            return $"user:{userId}";
        }

        // Non-authenticated traffic - which is what the public AI generation intake
        // and probes present as - is addressed by where it arrived from. That
        // address is the only client identifier this API reads anywhere (see
        // docs/AUTHENTICATION.md: forwarded client IP is not consumed), and a
        // partition key is held by the limiter for as long as the window it bounds,
        // so the address must be digested rather than retained verbatim. The salt is
        // per-process: keys, budgets and windows all live in memory and reset
        // together, so rotation cannot strand a client's spent permits.
        var peerAddress = context.Connection.RemoteIpAddress;
        if (peerAddress is not null)
        {
            return $"{AnonymousPartitionPrefix}:{Digest(NormalizeAddress(peerAddress))}";
        }

        // A transport that exposes no peer address (unix socket, in-memory host) is
        // still not one client. Falling back to a single literal key made every
        // caller on that transport share one budget, so the first four in-flight
        // anonymous generation requests 429-ed everyone else behind the socket. The
        // server-assigned connection id separates those neighbours without
        // introducing any client-supplied input: it is neither guessable nor
        // shareable by a caller, callers that can reach a unix socket at all are
        // already inside the boundary that grants the socket, and an idle partition
        // is reclaimed by the framework's limiter cache, so the key space stays
        // bounded by live connections rather than growing per request.
        var connectionId = context.Connection.Id;
        return string.IsNullOrWhiteSpace(connectionId)
            ? $"{AnonymousPartitionPrefix}:peerless"
            : $"{AnonymousPartitionPrefix}:conn-{Digest(connectionId)}";
    }

    // A dual-stack listener reports the same IPv4 caller as either a plain address or
    // its IPv4-mapped IPv6 form. Those are one client, and splitting them would let a
    // caller pick the transport that leaves its own budget untouched. Merging only
    // ever tightens a budget, so this cannot relax an existing limit.
    private static string NormalizeAddress(System.Net.IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();

    // 128 bits of keyed digest: collision-resistant across clients, and truncation
    // that keeps limiter keys and any future key-bearing diagnostic short.
    private static string Digest(string material) =>
        Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            AnonymousKeySalt, System.Text.Encoding.UTF8.GetBytes(material)))[..32].ToLowerInvariant();

    private const string AnonymousPartitionPrefix = "anon";
    private static readonly byte[] AnonymousKeySalt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
}
