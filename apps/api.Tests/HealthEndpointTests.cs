using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class HealthEndpointTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public HealthEndpointTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    public async Task Liveness_endpoints_are_anonymous_cache_safe_and_correlated(string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma);
        Assert.True(response.Headers.TryGetValues("X-Request-ID", out var requestIds));
        var requestId = Assert.Single(requestIds);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alive", body.GetProperty("status").GetString());
        Assert.Equal("Taslim API", body.GetProperty("service").GetString());
        Assert.Equal(requestId, body.GetProperty("requestId").GetString());
        Assert.Equal("process", body.GetProperty("checks")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Readiness_is_anonymous_cache_safe_and_sanitizes_dependency_failures()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        var rawBody = await response.Content.ReadAsStringAsync();

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable });
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma);
        Assert.DoesNotContain("password", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", rawBody, StringComparison.OrdinalIgnoreCase);

        using var body = JsonDocument.Parse(rawBody);
        var status = body.RootElement.GetProperty("status").GetString();
        Assert.True(status is "ready" or "not_ready");
        Assert.Equal("Taslim API", body.RootElement.GetProperty("service").GetString());
    }
}
