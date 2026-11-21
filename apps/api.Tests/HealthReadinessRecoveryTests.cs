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

public sealed class HealthReadinessRecoveryApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Health:ProbeTimeoutSeconds", "1");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService, FailOnceReadinessStorage>();
        });
    }
}

public sealed class HealthReadinessRecoveryTests : IClassFixture<HealthReadinessRecoveryApiFactory>
{
    private readonly HealthReadinessRecoveryApiFactory factory;

    public HealthReadinessRecoveryTests(HealthReadinessRecoveryApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Readiness_maps_temporary_storage_unavailability_to_503_then_recovers()
    {
        using var client = factory.CreateClient();
        var storage = (FailOnceReadinessStorage)factory.Services.GetRequiredService<IFileStorageService>();

        using var unavailable = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", unavailable.Headers.Pragma.ToString());
        Assert.True(unavailable.Headers.TryGetValues("X-Request-ID", out var unavailableRequestIds));
        var unavailableRequestId = Assert.Single(unavailableRequestIds);
        using (var body = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync()))
        {
            Assert.Equal("not_ready", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(unavailableRequestId, body.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("unconfigured", body.RootElement.GetProperty("checks")[1].GetProperty("status").GetString());
            var raw = body.RootElement.GetRawText();
            Assert.DoesNotContain("test-storage-private-id", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FileStorageUnavailableException", raw, StringComparison.Ordinal);
        }

        using var recovered = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("no-store", recovered.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", recovered.Headers.Pragma.ToString());
        Assert.True(recovered.Headers.TryGetValues("X-Request-ID", out var recoveredRequestIds));
        var recoveredRequestId = Assert.Single(recoveredRequestIds);
        Assert.NotEqual(unavailableRequestId, recoveredRequestId);
        using (var body = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync()))
        {
            Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(recoveredRequestId, body.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("available", body.RootElement.GetProperty("checks")[1].GetProperty("status").GetString());
        }

        Assert.Equal(2, storage.ProbeCount);
        Assert.Equal(0, storage.WriteCount);
    }
}

internal sealed class FailOnceReadinessStorage : IFileStorageService
{
    private int probeCount;
    private int writeCount;

    public string ProviderKey => "test-storage-private-id";
    public int ProbeCount => Volatile.Read(ref probeCount);
    public int WriteCount => Volatile.Read(ref writeCount);

    public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref writeCount);
        throw new NotSupportedException("Readiness smoke must not write storage.");
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref probeCount) == 1)
        {
            throw new FileStorageUnavailableException();
        }

        return Task.FromResult(true);
    }
}
