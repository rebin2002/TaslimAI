using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free negative contract tests for the Taslim completion-event receiver.
/// These exercise only the local API test host and do not prove any external
/// Manus Bridge publisher, native task-ID mapping, or Task Detail credential scope.
/// </summary>
public sealed class ManusBridgeCompletionSecurityContractTests : IClassFixture<TaslimApiFactory>
{
    private const string SecretEnvironmentVariable = "AUTOPILOT_WEBHOOK_SECRET";
    private const string TestSecret = "isolated-completion-security-contract-test-secret";
    private const string Source = "completion-security-contract-test";

    private readonly TaslimApiFactory factory;

    public ManusBridgeCompletionSecurityContractTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Tampered_payload_with_original_hmac_is_rejected_without_queueing()
    {
        var previousSecret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        Environment.SetEnvironmentVariable(SecretEnvironmentVariable, TestSecret);
        var eventId = $"evt-tampered-{Guid.NewGuid():N}";
        try
        {
            using var client = factory.CreateClient();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signedPayload = CompletionPayload("wave-tampered", "task-tampered", "succeeded");
            var transmittedPayload = CompletionPayload("wave-tampered", "task-tampered", "failed");
            var signature = Sign(TestSecret, timestamp, signedPayload);

            using var request = CreateRequest(eventId, timestamp, signature, transmittedPayload);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await AssertNoStoredEventAsync(eventId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvironmentVariable, previousSecret);
        }
    }

    [Fact]
    public async Task Malformed_json_with_valid_hmac_is_rejected_without_queueing()
    {
        var previousSecret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        Environment.SetEnvironmentVariable(SecretEnvironmentVariable, TestSecret);
        var eventId = $"evt-malformed-{Guid.NewGuid():N}";
        try
        {
            using var client = factory.CreateClient();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            const string malformedPayload = "{\"waveKey\":";
            var signature = Sign(TestSecret, timestamp, malformedPayload);

            using var request = CreateRequest(eventId, timestamp, signature, malformedPayload);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("AUTOPILOT_EVENT_REJECTED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            await AssertNoStoredEventAsync(eventId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvironmentVariable, previousSecret);
        }
    }

    [Fact]
    public async Task Valid_hmac_with_future_timestamp_outside_tolerance_is_rejected_without_queueing()
    {
        var previousSecret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        Environment.SetEnvironmentVariable(SecretEnvironmentVariable, TestSecret);
        var eventId = $"evt-future-{Guid.NewGuid():N}";
        try
        {
            using var client = factory.CreateClient();
            var timestamp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var payload = CompletionPayload("wave-future", "task-future", "succeeded");
            var signature = Sign(TestSecret, timestamp, payload);

            using var request = CreateRequest(eventId, timestamp, signature, payload);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("AUTOPILOT_EVENT_REJECTED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            await AssertNoStoredEventAsync(eventId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvironmentVariable, previousSecret);
        }
    }

    [Fact]
    public async Task Reused_event_id_with_different_wave_and_task_identity_is_rejected_without_mutation()
    {
        var previousSecret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        Environment.SetEnvironmentVariable(SecretEnvironmentVariable, TestSecret);
        var eventId = $"evt-identity-conflict-{Guid.NewGuid():N}";
        var originalWaveKey = $"wave-original-{Guid.NewGuid():N}";
        var originalTaskId = $"task-original-{Guid.NewGuid():N}";
        var conflictingWaveKey = $"wave-conflicting-{Guid.NewGuid():N}";
        var conflictingTaskId = $"task-conflicting-{Guid.NewGuid():N}";
        try
        {
            using var client = factory.CreateClient();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var originalPayload = CompletionPayload(originalWaveKey, originalTaskId, "succeeded");
            var conflictingPayload = CompletionPayload(conflictingWaveKey, conflictingTaskId, "failed");

            using var originalRequest = CreateRequest(
                eventId,
                timestamp,
                Sign(TestSecret, timestamp, originalPayload),
                originalPayload);
            using var originalResponse = await client.SendAsync(originalRequest);
            Assert.Equal(HttpStatusCode.Accepted, originalResponse.StatusCode);

            // A valid signature cannot make an existing event ID mean a different
            // wave/task. The first durable receipt remains immutable.
            using var conflictingRequest = CreateRequest(
                eventId,
                timestamp,
                Sign(TestSecret, timestamp, conflictingPayload),
                conflictingPayload);
            using var conflictingResponse = await client.SendAsync(conflictingRequest);
            Assert.Equal(HttpStatusCode.BadRequest, conflictingResponse.StatusCode);
            using (var error = JsonDocument.Parse(await conflictingResponse.Content.ReadAsStringAsync()))
            {
                Assert.Equal("AUTOPILOT_EVENT_REJECTED", error.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var storedEvents = await db.AutopilotEvents
                .Where(item => item.SourceSystem == Source && item.ExternalEventId == eventId)
                .ToListAsync();
            var stored = Assert.Single(storedEvents);
            Assert.Equal(originalPayload, stored.SignalJson);
            Assert.Equal(originalWaveKey, stored.WaveKey);
            Assert.Equal(originalTaskId, stored.TaskId);
            Assert.Equal(AutopilotEventStatuses.Queued, stored.Status);
            Assert.True(stored.SignatureVerified);
            Assert.True(stored.ReplayProtected);

            Assert.Empty(await db.AutopilotRuns.Where(item =>
                item.WaveKey == originalWaveKey || item.WaveKey == conflictingWaveKey).ToListAsync());
            Assert.Empty(await db.AutopilotWaveTasks.Where(item =>
                item.WaveKey == originalWaveKey || item.WaveKey == conflictingWaveKey
                || item.TaskId == originalTaskId || item.TaskId == conflictingTaskId).ToListAsync());
            Assert.Empty(await db.AutopilotGateEvaluations.Where(item =>
                item.WaveKey == originalWaveKey || item.WaveKey == conflictingWaveKey).ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvironmentVariable, previousSecret);
        }
    }

    private async Task AssertNoStoredEventAsync(string eventId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await db.AutopilotEvents.Where(item =>
            item.SourceSystem == Source && item.ExternalEventId == eventId).ToListAsync());
    }

    private static HttpRequestMessage CreateRequest(string eventId, string timestamp, string signature, string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/autopilot/events")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Autopilot-Source", Source);
        request.Headers.Add("X-Autopilot-Event-Id", eventId);
        request.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
        request.Headers.Add("X-Autopilot-Timestamp", timestamp);
        request.Headers.Add("X-Autopilot-Signature", signature);
        return request;
    }

    private static string CompletionPayload(string waveKey, string taskId, string outcome) =>
        JsonSerializer.Serialize(new { waveKey, taskId, outcome });

    private static string Sign(string secret, string timestamp, string rawPayload)
    {
        var signedBytes = Encoding.UTF8.GetBytes($"{timestamp}.{rawPayload}");
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedBytes);
        return $"sha256={Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
