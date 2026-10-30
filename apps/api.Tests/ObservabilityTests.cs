using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Files;
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
    public async Task Liveness_uses_a_server_owned_request_id_without_querying_dependencies()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Request-ID", "incident-review-123");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requestId = response.Headers.GetValues("X-Request-ID").Single();
        Assert.NotEqual("incident-review-123", requestId);
        Assert.True(Guid.TryParseExact(requestId, "N", out _));
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("alive", body.GetProperty("status").GetString());
        Assert.Equal(requestId, body.GetProperty("requestId").GetString());
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

public sealed class LivenessStorageUnavailableApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService, AlwaysUnavailableHealthProbeStorage>();
        });
    }
}

public sealed class LivenessStorageIndependenceTests : IClassFixture<LivenessStorageUnavailableApiFactory>
{
    private readonly LivenessStorageUnavailableApiFactory factory;

    public LivenessStorageIndependenceTests(LivenessStorageUnavailableApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    public async Task Liveness_remains_healthy_without_touching_unavailable_storage(string path)
    {
        using var client = factory.CreateClient();
        var storage = (AlwaysUnavailableHealthProbeStorage)factory.Services.GetRequiredService<IFileStorageService>();

        using var response = await client.GetAsync(path);
        var rawBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(storage.ProviderKey, rawBody, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(rawBody);
        Assert.Equal("alive", body.RootElement.GetProperty("status").GetString());
        var checks = body.RootElement.GetProperty("checks");
        Assert.Equal(1, checks.GetArrayLength());
        Assert.Equal("process", checks[0].GetProperty("name").GetString());
        Assert.Equal("alive", checks[0].GetProperty("status").GetString());
        Assert.Equal(0, storage.OperationCount);
    }
}

internal sealed class AlwaysUnavailableHealthProbeStorage : IFileStorageService
{
    private int operationCount;

    public string ProviderKey => "synthetic-storage-private-marker";
    public int OperationCount => Volatile.Read(ref operationCount);

    public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Unavailable();

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Unavailable<Stream?>();

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Unavailable();

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Unavailable<bool>();

    private Task Unavailable()
    {
        Interlocked.Increment(ref operationCount);
        return Task.FromException(new FileStorageUnavailableException());
    }

    private Task<T> Unavailable<T>()
    {
        Interlocked.Increment(ref operationCount);
        return Task.FromException<T>(new FileStorageUnavailableException());
    }
}
