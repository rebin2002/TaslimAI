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

    [Fact]
    public async Task Candidate_v2_hmac_is_not_accepted_by_the_current_v1_http_receiver()
    {
        var previous = Environment.GetEnvironmentVariable(SecretVariable);
        Environment.SetEnvironmentVariable(SecretVariable, Secret);
        try
        {
            using var isolatedFactory = new AutopilotAcknowledgementFactory();
            using var client = isolatedFactory.CreateClient();
            using (var setupScope = isolatedFactory.Services.CreateScope())
                setupScope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

            // Keep the v2 canonical fields/body but use a fresh timestamp so the v1
            // endpoint rejects the candidate signature, not just a stale fixture.
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var rawBody = Encoding.UTF8.GetBytes(AutopilotCandidateV2OfflineParityTests.RawBodyText);
            var signature = Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(Secret),
                AutopilotCandidateV2OfflineParityTests.CandidateSigningInput(
                    timestamp,
                    AutopilotCandidateV2OfflineParityTests.Source,
                    AutopilotCandidateV2OfflineParityTests.EventType,
                    AutopilotCandidateV2OfflineParityTests.ExternalEventId,
                    rawBody))).ToLowerInvariant();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/autopilot/events")
            {
                Content = new ByteArrayContent(rawBody)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Autopilot-Source", AutopilotCandidateV2OfflineParityTests.Source);
            request.Headers.Add("X-Autopilot-Event-Id", AutopilotCandidateV2OfflineParityTests.ExternalEventId);
            request.Headers.Add("X-Autopilot-Event-Type", AutopilotCandidateV2OfflineParityTests.EventType);
            request.Headers.Add("X-Autopilot-Timestamp", timestamp);
            request.Headers.Add("X-Autopilot-Signature", $"sha256={signature}");

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("AUTOPILOT_EVENT_UNAUTHENTICATED",
                responseBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var scope = isolatedFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Empty(await db.AutopilotEvents.AsNoTracking().ToListAsync());
            Assert.Empty(await db.AutopilotWaveLaunchTasks.AsNoTracking().ToListAsync());
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

public sealed class AutopilotCandidateV2OfflineParityTests
{
    private const string TestOnlySecret = "provider-free-external-ack-contract-secret";
    private const string Timestamp = "1794067200";
    internal const string Source = "manus-bridge";
    internal const string EventType = "wave.task.completed";
    internal const string ExternalEventId = "evt-v2-contract-001";
    internal const string RawBodyText = """{"waveKey":"wave-v2-contract","taskId":"task-v2-contract","outcome":"succeeded","evidence":"{\"kind\":\"taslim.manus.task_detail_terminal_receipt.v1\",\"task_id\":\"ABCDEFGHIJKLMNOPQRSTUV\",\"agent_status\":\"stopped\",\"stop_reason\":\"finish\",\"has_running_background_jobs\":false,\"event_id\":\"evt-v2-contract-001\"}"}""";
    private const string ExpectedBodySha256 = "abc18a1912a315c286b20dae4e7d0c4b395df20c20e8737d40d619a01a0fde87";
    private const string ExpectedHmac = "0f45f43333c9a6c97b0ca8720758ceb6c8daeb675097207c84c7d69efdb51c47";

    // This is an offline helper-parity candidate only. It is not an accepted HTTP
    // signing contract and does not invoke or change the current v1 receiver.
    [Fact]
    public void Candidate_v2_fixture_pins_exact_raw_bytes_and_hmac_and_metadata_or_body_mutations_change_it()
    {
        var rawBody = Encoding.UTF8.GetBytes(RawBodyText);
        Assert.Equal(42, Encoding.UTF8.GetByteCount(TestOnlySecret));
        Assert.Equal(323, rawBody.Length);
        var bodySha256 = Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();
        Assert.Equal(ExpectedBodySha256, bodySha256);

        var canonical = CandidateSigningInput(Timestamp, Source, EventType, ExternalEventId, rawBody);
        const string expectedCanonical =
            "taslim-autopilot-v2\n" +
            "1794067200\n" +
            "12:manus-bridge\n" +
            "19:wave.task.completed\n" +
            "19:evt-v2-contract-001\n" +
            "abc18a1912a315c286b20dae4e7d0c4b395df20c20e8737d40d619a01a0fde87\n";
        Assert.Equal(158, canonical.Length);
        Assert.Equal(expectedCanonical, Encoding.UTF8.GetString(canonical));

        var key = Encoding.UTF8.GetBytes(TestOnlySecret);
        var baselineHmac = Convert.ToHexString(HMACSHA256.HashData(key, canonical)).ToLowerInvariant();
        Assert.Equal(ExpectedHmac, baselineHmac);

        var mutations = new (string Name, string Timestamp, string Source, string EventType, string ExternalEventId, byte[] Body)[]
        {
            ("source", Timestamp, "other-bridge", EventType, ExternalEventId, rawBody),
            ("type", Timestamp, Source, "wave.task.failed", ExternalEventId, rawBody),
            ("event id", Timestamp, Source, EventType, "evt-other", rawBody),
            ("timestamp", "1794067201", Source, EventType, ExternalEventId, rawBody),
            ("body", Timestamp, Source, EventType, ExternalEventId, Encoding.UTF8.GetBytes(RawBodyText + " "))
        };

        foreach (var mutation in mutations)
        {
            var mutatedInput = CandidateSigningInput(
                mutation.Timestamp,
                mutation.Source,
                mutation.EventType,
                mutation.ExternalEventId,
                mutation.Body);
            var mutatedHmac = Convert.ToHexString(HMACSHA256.HashData(key, mutatedInput)).ToLowerInvariant();
            Assert.NotEqual(baselineHmac, mutatedHmac);
        }
    }

    internal static byte[] CandidateSigningInput(
        string timestamp,
        string source,
        string eventType,
        string externalEventId,
        byte[] rawBody)
    {
        var digest = Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();
        var canonical =
            $"taslim-autopilot-v2\n{timestamp}\n" +
            $"{Encoding.UTF8.GetByteCount(source)}:{source}\n" +
            $"{Encoding.UTF8.GetByteCount(eventType)}:{eventType}\n" +
            $"{Encoding.UTF8.GetByteCount(externalEventId)}:{externalEventId}\n" +
            $"{digest}\n";
        return Encoding.UTF8.GetBytes(canonical);
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
