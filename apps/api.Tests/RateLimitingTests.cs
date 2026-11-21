using System.Security.Claims;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class RateLimitingTests
{
    [Fact]
    public async Task Expensive_policy_rejects_when_user_concurrency_is_full_without_queueing()
    {
        var options = CreateOptions();
        var context = CreateContext(RateLimiting.ExpensiveAi, Guid.NewGuid());
        var limiter = options.GlobalLimiter!;

        using var first = await limiter.AcquireAsync(context);
        using var second = await limiter.AcquireAsync(context);
        using var rejected = await limiter.AcquireAsync(context);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    public async Task Health_policy_rejects_when_anonymous_client_concurrency_is_full_without_queueing()
    {
        var options = CreateOptions();
        var context = CreateAnonymousContext(RateLimiting.Health, "192.0.2.10");
        var limiter = options.GlobalLimiter!;

        using var first = await limiter.AcquireAsync(context);
        using var second = await limiter.AcquireAsync(context);
        using var rejected = await limiter.AcquireAsync(context);

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(rejected.IsAcquired);
    }

    [Fact]
    public async Task Concurrency_partition_isolated_by_authenticated_user()
    {
        var options = CreateOptions();
        var limiter = options.GlobalLimiter!;
        var firstUser = CreateContext(RateLimiting.Chat, Guid.NewGuid());
        var secondUser = CreateContext(RateLimiting.Chat, Guid.NewGuid());

        using var firstUserLease = await limiter.AcquireAsync(firstUser);
        using var firstUserSecondLease = await limiter.AcquireAsync(firstUser);
        using var secondUserLease = await limiter.AcquireAsync(secondUser);

        Assert.True(firstUserLease.IsAcquired);
        Assert.True(firstUserSecondLease.IsAcquired);
        Assert.True(secondUserLease.IsAcquired);
    }

    [Fact]
    public async Task Account_security_concurrency_isolated_by_authenticated_user()
    {
        var options = CreateOptions();
        var limiter = options.GlobalLimiter!;
        var firstUser = CreateContext(RateLimiting.AccountSecurity, Guid.NewGuid());
        var secondUser = CreateContext(RateLimiting.AccountSecurity, Guid.NewGuid());

        using var firstUserLease = await limiter.AcquireAsync(firstUser);
        using var firstUserRejected = await limiter.AcquireAsync(firstUser);
        using var secondUserLease = await limiter.AcquireAsync(secondUser);

        Assert.True(firstUserLease.IsAcquired);
        Assert.False(firstUserRejected.IsAcquired);
        Assert.True(secondUserLease.IsAcquired);
    }

    [Fact]
    public async Task Unprotected_endpoint_bypasses_global_concurrency_limiter()
    {
        var options = CreateOptions();
        var context = new DefaultHttpContext();

        using var lease = await options.GlobalLimiter!.AcquireAsync(context);

        Assert.True(lease.IsAcquired);
    }

    [Fact]
    public async Task Rejection_callback_returns_safe_json_with_retry_after()
    {
        var options = CreateOptions();
        var context = CreateContext(RateLimiting.ExpensiveAi, Guid.NewGuid());
        context.Response.Body = new MemoryStream();
        var limiter = options.GlobalLimiter!;

        using var first = await limiter.AcquireAsync(context);
        using var second = await limiter.AcquireAsync(context);
        using var rejected = await limiter.AcquireAsync(context);
        context.Response.StatusCode = options.RejectionStatusCode;

        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("60", context.Response.Headers.RetryAfter.ToString());
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("RATE_LIMITED", body, StringComparison.Ordinal);
        Assert.Contains("Too many requests", body, StringComparison.Ordinal);
    }

    private static RateLimiterOptions CreateOptions()
    {
        var options = new RateLimiterOptions();
        RateLimiting.Configure(options);
        return options;
    }

    private static HttpContext CreateContext(string policyName, Guid userId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString("N"))],
                "test")),
        };
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(policyName)),
            displayName: policyName));
        return context;
    }

    private static HttpContext CreateAnonymousContext(string policyName, string ipAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ipAddress);
        context.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new EnableRateLimitingAttribute(policyName)),
            displayName: policyName));
        return context;
    }
}
