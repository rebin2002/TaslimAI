using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AutopilotExternalEventAcknowledgementTests : IClassFixture<AutopilotAcknowledgementFactory>
{
    private const string SecretVariable = "AUTOPILOT_WEBHOOK_SECRET";
    private const string Secret = "provider-free-external-ack-contract-secret";
    private readonly AutopilotAcknowledgementFactory factory;

    public AutopilotExternalEventAcknowledgementTests(AutopilotAcknowledgementFactory factory)
    {
        this.factory = factory;
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_and_duplicate_ack_bind_the_same_external_id_to_the_durable_internal_guid(bool padHeader)
    {
        var previous = Environment.GetEnvironmentVariable(SecretVariable);
        Environment.SetEnvironmentVariable(SecretVariable, Secret);
        try
        {
            using var client = factory.CreateClient();
            var externalId = $"evt-ack-{Guid.NewGuid():N}";
            var payload = Payload(externalId);
            using var firstRequest = Request(padHeader ? $" {externalId} " : externalId, payload);
            using var first = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            using var accepted = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.Equal("accepted", accepted.RootElement.GetProperty("status").GetString());
            Assert.Equal(externalId, accepted.RootElement.GetProperty("externalEventId").GetString());
            var internalText = accepted.RootElement.GetProperty("eventId").GetString();
            Assert.True(Guid.TryParse(internalText, out var internalId));
            Assert.NotEqual(Guid.Empty, internalId);
            Assert.NotEqual(externalId, internalText);

            using var duplicateRequest = Request(externalId, payload);
            using var duplicate = await client.SendAsync(duplicateRequest);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            using var replay = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
            Assert.Equal("duplicate", replay.RootElement.GetProperty("status").GetString());
            Assert.Equal(externalId, replay.RootElement.GetProperty("externalEventId").GetString());
            Assert.Equal(internalText, replay.RootElement.GetProperty("eventId").GetString());

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var stored = Assert.Single(await db.AutopilotEvents.AsNoTracking().Where(item =>
                item.SourceSystem == ManusBridgeWaveLaunchProvider.Key && item.ExternalEventId == externalId).ToListAsync());
            Assert.Equal(internalId, stored.Id);
            Assert.Equal(payload, stored.SignalJson);
            Assert.True(stored.SignatureVerified);
            Assert.True(stored.ReplayProtected);
            Assert.Equal(AutopilotEventStatuses.Queued, stored.Status);
            Assert.Empty(await db.AutopilotWaveLaunchTasks.ToListAsync());
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, previous); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different-receipt-event")]
    public async Task Missing_or_mismatched_receipt_identity_has_no_success_ack_or_durable_event(string? receiptId)
    {
        var previous = Environment.GetEnvironmentVariable(SecretVariable);
        Environment.SetEnvironmentVariable(SecretVariable, Secret);
        try
        {
            using var client = factory.CreateClient();
            var externalId = $"evt-reject-ack-{Guid.NewGuid():N}";
            using var request = Request(externalId, Payload(receiptId));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.TryGetProperty("externalEventId", out _));
            Assert.False(body.RootElement.TryGetProperty("eventId", out _));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.False(await db.AutopilotEvents.AnyAsync(item => item.ExternalEventId == externalId));
            Assert.Empty(await db.AutopilotWaveLaunchTasks.ToListAsync());
        }
        finally { Environment.SetEnvironmentVariable(SecretVariable, previous); }
    }

    private static string Payload(string? receiptId) => JsonSerializer.Serialize(new
    {
        waveKey = "wave-ack-contract",
        taskId = "logical-ack-task",
        outcome = "succeeded",
        evidence = JsonSerializer.Serialize(new
        {
            kind = "taslim.manus.task_detail_terminal_receipt.v1",
            task_id = "ABCDEFGHIJKLMNOPQRSTUV",
            agent_status = "stopped",
            stop_reason = "finish",
            has_running_background_jobs = false,
            event_id = receiptId
        })
    });

    private static HttpRequestMessage Request(string externalId, string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/autopilot/events")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Autopilot-Source", ManusBridgeWaveLaunchProvider.Key);
        request.Headers.Add("X-Autopilot-Event-Id", externalId);
        request.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
        request.Headers.Add("X-Autopilot-Timestamp", timestamp);
        request.Headers.Add("X-Autopilot-Signature", $"sha256={Convert.ToHexString(signature).ToLowerInvariant()}");
        return request;
    }
}

// Each request/context owns its SQLite connection. Sharing one opened connection
// lets request-scope cleanup race EF's per-connection function registration.
public sealed class AutopilotAcknowledgementFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-ack-{Guid.NewGuid():N}.sqlite");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Autopilot:WatchdogEnabled"] = "false" }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite($"Data Source={databasePath};Default Timeout=30"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
        }
    }
}
