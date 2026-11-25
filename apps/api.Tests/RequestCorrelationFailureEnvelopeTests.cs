using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Taslim.Api.Tests;

// Incident correlation boundary: every API response, including the generic
// INTERNAL_ERROR envelope produced for unhandled failures, must carry the
// server-owned X-Request-ID that operational logs are keyed on. Otherwise a
// customer quoting a 500 has no quotable handle onto the audit trail, and the
// RequestId logged by the exception handler becomes unreachable.
public sealed class RequestCorrelationFailureEnvelopeTests
{
    private const string SyntheticFailurePath = "/api/test-only/unhandled-failure";

    // Deterministic synthetic trigger: a test-host-only terminal middleware
    // that throws for one reserved path. Real endpoints are untouched because
    // matched routes short-circuit before this terminal runs.
    private sealed class SyntheticFailureTerminalFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            next(builder);
            builder.Run(async context =>
            {
                if (context.Request.Path.StartsWithSegments(SyntheticFailurePath))
                {
                    throw new InvalidOperationException("synthetic unhandled failure for correlation coverage");
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync(string.Empty);
            });
        };
    }

    [Fact]
    public async Task Unhandled_failure_envelope_still_carries_the_server_owned_request_id()
    {
        using var factory = new TaslimApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
                services.AddTransient<IStartupFilter, SyntheticFailureTerminalFilter>());
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SyntheticFailurePath);
        request.Headers.Add("X-Request-ID", "caller-supplied-incident-id");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // The header must be present and server-generated: a caller-supplied
        // identifier is never echoed back into the audit boundary.
        var requestId = Assert.Single(response.Headers.GetValues("X-Request-ID"));
        Assert.NotEqual("caller-supplied-incident-id", requestId);
        Assert.True(Guid.TryParseExact(requestId, "N", out _));

        // Sanitized envelope contract: generic code only, no exception text,
        // stack, or provider detail leaked to the client.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic unhandled failure", body.ToString());
        Assert.DoesNotContain("stack", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
