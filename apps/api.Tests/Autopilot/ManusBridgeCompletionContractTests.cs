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
/// Contract guard for the documented Taslim receiver used by a completion bridge.
/// This exercises only the local API test host; it does not represent or verify
/// the existence of an external Manus Bridge publisher.
/// </summary>
public sealed class ManusBridgeCompletionContractTests : IClassFixture<TaslimApiFactory>
{
    private const string SecretEnvironmentVariable = "AUTOPILOT_WEBHOOK_SECRET";
    private const string TestSecret = "isolated-manus-bridge-contract-test-secret";
    private readonly TaslimApiFactory factory;

    public ManusBridgeCompletionContractTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Documented_signed_post_is_accepted_and_same_event_id_retry_is_idempotent()
    {
        var previousSecret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        Environment.SetEnvironmentVariable(SecretEnvironmentVariable, TestSecret);
        try
        {
            using var client = factory.CreateClient();
            var source = "completion-bridge-contract-test";
            var eventId = $"evt-{Guid.NewGuid():N}";
            var payload = $$"""{"waveKey":"bridge-contract-{{Guid.NewGuid():N}}","taskId":"manus-task-{{Guid.NewGuid():N}}","outcome":"succeeded"}""";
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signature = Sign(TestSecret, timestamp, payload);

            using var firstRequest = CreateSignedRequest(source, eventId, timestamp, signature, payload);
            using var firstResponse = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
            using var accepted = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            Assert.Equal("accepted", accepted.RootElement.GetProperty("status").GetString());
            var acceptedEventId = accepted.RootElement.GetProperty("eventId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(acceptedEventId));

            // A retry must preserve the source/event identity and exact signed body.
            using var retryRequest = CreateSignedRequest(source, eventId, timestamp, signature, payload);
            using var retryResponse = await client.SendAsync(retryRequest);
            Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
            using var duplicate = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
            Assert.Equal("duplicate", duplicate.RootElement.GetProperty("status").GetString());
            Assert.Equal(acceptedEventId, duplicate.RootElement.GetProperty("eventId").GetString());

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var stored = await db.AutopilotEvents.SingleAsync(item =>
                item.SourceSystem == source && item.ExternalEventId == eventId);
            Assert.Equal(AutopilotEventTypes.WaveTaskCompleted, stored.EventType);
            Assert.Equal(AutopilotEventStatuses.Queued, stored.Status);
            Assert.Equal(payload, stored.SignalJson);
            Assert.True(stored.SignatureVerified);
            Assert.True(stored.ReplayProtected);
            Assert.Equal(1, await db.AutopilotEvents.CountAsync(item =>
                item.SourceSystem == source && item.ExternalEventId == eventId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvironmentVariable, previousSecret);
        }
    }

    [Fact]
    public async Task Missing_signing_secret_fails_closed_without_queueing_the_event()
    {
        var previousSecret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        Environment.SetEnvironmentVariable(SecretEnvironmentVariable, null);
        try
        {
            using var client = factory.CreateClient();
            var source = "completion-bridge-contract-test";
            var eventId = $"evt-unconfigured-{Guid.NewGuid():N}";
            var payload = $$"""{"waveKey":"bridge-unconfigured-{{Guid.NewGuid():N}}","taskId":"manus-task-{{Guid.NewGuid():N}}","outcome":"succeeded"}""";
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            // Deliberately sign with a non-configured value. The receiver has no
            // valid key and must reject rather than accept or persist this event.
            var signature = Sign(TestSecret, timestamp, payload);

            using var request = CreateSignedRequest(source, eventId, timestamp, signature, payload);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Empty(await db.AutopilotEvents.Where(item =>
                item.SourceSystem == source && item.ExternalEventId == eventId).ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretEnvironmentVariable, previousSecret);
        }
    }

    private static HttpRequestMessage CreateSignedRequest(
        string source,
        string eventId,
        string timestamp,
        string signature,
        string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/autopilot/events")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Autopilot-Source", source);
        request.Headers.Add("X-Autopilot-Event-Id", eventId);
        request.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
        request.Headers.Add("X-Autopilot-Timestamp", timestamp);
        request.Headers.Add("X-Autopilot-Signature", signature);
        return request;
    }

    private static string Sign(string secret, string timestamp, string rawPayload)
    {
        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{rawPayload}");
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signedPayload);
        return $"sha256={Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
