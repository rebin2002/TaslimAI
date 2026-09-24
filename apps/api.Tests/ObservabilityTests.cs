using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ObservabilityTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ObservabilityTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Liveness_echoes_a_safe_request_id_without_querying_dependencies()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Request-ID", "observability-test-123");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("observability-test-123", response.Headers.GetValues("X-Request-ID").Single());
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("alive", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_reports_dependency_states_without_secrets()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ready\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\":\"database\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\":\"storage\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secretKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", json, StringComparison.OrdinalIgnoreCase);
    }
}
