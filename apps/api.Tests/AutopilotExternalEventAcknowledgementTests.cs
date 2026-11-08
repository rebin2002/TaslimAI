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
    public async Task Legacy_experimental_not_accepted_v2_hmac_is_not_accepted_by_the_current_v1_http_receiver()
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
            var rawBody = Encoding.UTF8.GetBytes(AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedRawBodyText);
            var signature = Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(Secret),
                AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedSigningInput(
                    timestamp,
                    AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedSource,
                    AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedEventType,
                    AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedExternalEventId,
                    rawBody))).ToLowerInvariant();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/autopilot/events")
            {
                Content = new ByteArrayContent(rawBody)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Autopilot-Source", AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedSource);
            request.Headers.Add("X-Autopilot-Event-Id", AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedExternalEventId);
            request.Headers.Add("X-Autopilot-Event-Type", AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedEventType);
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

    // Characterization only: source/type acceptance below records the current v1
    // metadata-authentication gap, not an approved signing contract.
    [Theory]
    [InlineData("source", "alternate-source", 202, "alternate-source", "wave.task.completed", "evt-v1-header-mutation")]
    [InlineData("type", "wave.completed", 202, "manus-bridge", "wave.completed", "evt-v1-header-mutation")]
    [InlineData("event-id", "evt-v1-header-mutated", 400, null, null, null)]
    public async Task Current_v1_metadata_header_mutations_are_characterized_without_provider_calls(
        string mutation,
        string mutatedValue,
        int expectedStatusCode,
        string? expectedPersistedSource,
        string? expectedPersistedEventType,
        string? expectedPersistedExternalEventId)
    {
        var previous = Environment.GetEnvironmentVariable(SecretVariable);
        Environment.SetEnvironmentVariable(SecretVariable, Secret);
        try
        {
            using var isolatedFactory = new AutopilotAcknowledgementFactory();
            using var client = isolatedFactory.CreateClient();
            using (var setupScope = isolatedFactory.Services.CreateScope())
                setupScope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

            using (var beforeScope = isolatedFactory.Services.CreateScope())
            {
                var beforeDb = beforeScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                Assert.Empty(await beforeDb.AutopilotEvents.AsNoTracking().ToListAsync());
                Assert.Empty(await beforeDb.AutopilotWaveLaunchTasks.AsNoTracking().ToListAsync());
            }

            const string signedExternalEventId = "evt-v1-header-mutation";
            var payload = Payload(signedExternalEventId);
            using var request = Request(signedExternalEventId, payload);
            var headerName = mutation switch
            {
                "source" => "X-Autopilot-Source",
                "type" => "X-Autopilot-Event-Type",
                "event-id" => "X-Autopilot-Event-Id",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown test header mutation.")
            };
            Assert.True(request.Headers.Remove(headerName));
            request.Headers.Add(headerName, mutatedValue);

            Assert.Equal(
                mutation == "source" ? mutatedValue : ManusBridgeWaveLaunchProvider.Key,
                request.Headers.GetValues("X-Autopilot-Source").Single());
            Assert.Equal(
                mutation == "type" ? mutatedValue : AutopilotEventTypes.WaveTaskCompleted,
                request.Headers.GetValues("X-Autopilot-Event-Type").Single());
            Assert.Equal(
                mutation == "event-id" ? mutatedValue : signedExternalEventId,
                request.Headers.GetValues("X-Autopilot-Event-Id").Single());

            using var response = await client.SendAsync(request);
            Assert.Equal((HttpStatusCode)expectedStatusCode, response.StatusCode);
            using var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (expectedStatusCode == (int)HttpStatusCode.Accepted)
            {
                Assert.Equal("accepted", responseBody.RootElement.GetProperty("status").GetString());
                Assert.Equal(expectedPersistedExternalEventId,
                    responseBody.RootElement.GetProperty("externalEventId").GetString());
            }
            else
            {
                Assert.Equal("AUTOPILOT_EVENT_REJECTED",
                    responseBody.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            Assert.Equal(0, isolatedFactory.ProviderProbe.ProviderCalls);
            using var afterScope = isolatedFactory.Services.CreateScope();
            var afterDb = afterScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var events = await afterDb.AutopilotEvents.AsNoTracking().ToListAsync();
            if (expectedPersistedSource is null)
            {
                Assert.Empty(events);
            }
            else
            {
                var stored = Assert.Single(events);
                Assert.Equal(expectedPersistedSource, stored.SourceSystem);
                Assert.Equal(expectedPersistedEventType, stored.EventType);
                Assert.Equal(expectedPersistedExternalEventId, stored.ExternalEventId);
                Assert.Equal(payload, stored.SignalJson);
                Assert.True(stored.SignatureVerified);
                Assert.True(stored.ReplayProtected);
                Assert.Equal(AutopilotEventStatuses.Queued, stored.Status);
            }
            Assert.Empty(await afterDb.AutopilotWaveLaunchTasks.AsNoTracking().ToListAsync());
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

// LEGACY_EXPERIMENTAL_NOT_ACCEPTED: retained only to characterize the historical
// 323-byte / 158-byte helper. It is not the proposed integration contract.
public sealed class AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests
{
    private const string LegacyExperimentalNotAcceptedTestOnlySecret = "provider-free-external-ack-contract-secret";
    private const string LegacyExperimentalNotAcceptedTimestamp = "1794067200";
    internal const string LegacyExperimentalNotAcceptedSource = "manus-bridge";
    internal const string LegacyExperimentalNotAcceptedEventType = "wave.task.completed";
    internal const string LegacyExperimentalNotAcceptedExternalEventId = "evt-v2-contract-001";
    internal const string LegacyExperimentalNotAcceptedRawBodyText = """{"waveKey":"wave-v2-contract","taskId":"task-v2-contract","outcome":"succeeded","evidence":"{\"kind\":\"taslim.manus.task_detail_terminal_receipt.v1\",\"task_id\":\"ABCDEFGHIJKLMNOPQRSTUV\",\"agent_status\":\"stopped\",\"stop_reason\":\"finish\",\"has_running_background_jobs\":false,\"event_id\":\"evt-v2-contract-001\"}"}""";
    private const string LegacyExperimentalNotAcceptedExpectedBodySha256 = "abc18a1912a315c286b20dae4e7d0c4b395df20c20e8737d40d619a01a0fde87";
    private const string LegacyExperimentalNotAcceptedExpectedHmac = "0f45f43333c9a6c97b0ca8720758ceb6c8daeb675097207c84c7d69efdb51c47";

    // LEGACY_EXPERIMENTAL_NOT_ACCEPTED: this test pins only the retired helper
    // shape. Passing it is not v2 integration acceptance and says nothing about
    // Node/C# interoperability or the current HTTP receiver.
    [Fact]
    public void Legacy_experimental_not_accepted_fixture_pins_323_byte_body_158_byte_input_and_legacy_hmac()
    {
        var rawBody = Encoding.UTF8.GetBytes(LegacyExperimentalNotAcceptedRawBodyText);
        Assert.Equal(42, Encoding.UTF8.GetByteCount(LegacyExperimentalNotAcceptedTestOnlySecret));
        Assert.Equal(323, rawBody.Length);
        var bodySha256 = Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();
        Assert.Equal(LegacyExperimentalNotAcceptedExpectedBodySha256, bodySha256);

        var canonical = LegacyExperimentalNotAcceptedSigningInput(
            LegacyExperimentalNotAcceptedTimestamp,
            LegacyExperimentalNotAcceptedSource,
            LegacyExperimentalNotAcceptedEventType,
            LegacyExperimentalNotAcceptedExternalEventId,
            rawBody);
        const string expectedCanonical =
            "taslim-autopilot-v2\n" +
            "1794067200\n" +
            "12:manus-bridge\n" +
            "19:wave.task.completed\n" +
            "19:evt-v2-contract-001\n" +
            "abc18a1912a315c286b20dae4e7d0c4b395df20c20e8737d40d619a01a0fde87\n";
        Assert.Equal(158, canonical.Length);
        Assert.Equal(expectedCanonical, Encoding.UTF8.GetString(canonical));

        var key = Encoding.UTF8.GetBytes(LegacyExperimentalNotAcceptedTestOnlySecret);
        var baselineHmac = Convert.ToHexString(HMACSHA256.HashData(key, canonical)).ToLowerInvariant();
        Assert.Equal(LegacyExperimentalNotAcceptedExpectedHmac, baselineHmac);

        var mutations = new (string Name, string Timestamp, string Source, string EventType, string ExternalEventId, byte[] Body)[]
        {
            ("source", LegacyExperimentalNotAcceptedTimestamp, "other-bridge", LegacyExperimentalNotAcceptedEventType, LegacyExperimentalNotAcceptedExternalEventId, rawBody),
            ("type", LegacyExperimentalNotAcceptedTimestamp, LegacyExperimentalNotAcceptedSource, "wave.task.failed", LegacyExperimentalNotAcceptedExternalEventId, rawBody),
            ("event id", LegacyExperimentalNotAcceptedTimestamp, LegacyExperimentalNotAcceptedSource, LegacyExperimentalNotAcceptedEventType, "evt-other", rawBody),
            ("timestamp", "1794067201", LegacyExperimentalNotAcceptedSource, LegacyExperimentalNotAcceptedEventType, LegacyExperimentalNotAcceptedExternalEventId, rawBody),
            ("body", LegacyExperimentalNotAcceptedTimestamp, LegacyExperimentalNotAcceptedSource, LegacyExperimentalNotAcceptedEventType, LegacyExperimentalNotAcceptedExternalEventId, Encoding.UTF8.GetBytes(LegacyExperimentalNotAcceptedRawBodyText + " "))
        };

        foreach (var mutation in mutations)
        {
            var mutatedInput = LegacyExperimentalNotAcceptedSigningInput(
                mutation.Timestamp,
                mutation.Source,
                mutation.EventType,
                mutation.ExternalEventId,
                mutation.Body);
            var mutatedHmac = Convert.ToHexString(HMACSHA256.HashData(key, mutatedInput)).ToLowerInvariant();
            Assert.NotEqual(baselineHmac, mutatedHmac);
        }
    }

    internal static byte[] LegacyExperimentalNotAcceptedSigningInput(
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

// This is the sole proposed integration-acceptance vector in this file: the
// owner-supplied 369-byte / 166-byte method-and-path-bound form. Owners accepted
// it as offline test material; runtime support and activation remain deferred.
public sealed class AutopilotSoleProposedV2IntegrationAcceptanceVectorFixtureTests
{
    // The JSON is retained exactly as supplied; its original status field is not
    // a runtime decision. The 369/166 form is the sole proposed integration vector.
    private const string OwnerGoldenVectorJson = """
      {
        "status": "PROPOSED_UNAPPROVED_TEST_VECTOR",
        "values": {
          "version": "v2",
          "method": "POST",
          "path": "/api/autopilot/events",
          "timestamp": "1760000000",
          "source": "manus-bridge",
          "event_id": "evt:v2-001",
          "event_type": "wave.task.completed"
        },
        "headers": {
          "Content-Type": "application/json",
          "X-Autopilot-Signature-Version": "v2",
          "X-Autopilot-Timestamp": "1760000000",
          "X-Autopilot-Source": "manus-bridge",
          "X-Autopilot-Event-Id": "evt:v2-001",
          "X-Autopilot-Event-Type": "wave.task.completed",
          "X-Autopilot-Signature": "v2=ffb9595c31c404602bcbe2bac7e9729c7947a896eaa7ac4125ae95abd6551e65"
        },
        "raw_body": {
          "utf8": "{\"waveKey\":\"wave-demo-01\",\"taskId\":\"task-demo-01\",\"outcome\":\"succeeded\",\"evidence\":\"{\\\"kind\\\":\\\"taslim.manus.task_detail_terminal_receipt.v1\\\",\\\"task_id\\\":\\\"AbCdEf0123456789GhIjKl\\\",\\\"agent_status\\\":\\\"stopped\\\",\\\"stop_reason\\\":\\\"finish\\\",\\\"has_running_background_jobs\\\":false,\\\"event_id\\\":\\\"evt:v2-001\\\"}\",\"checks\":{},\"requiresHumanDecision\":false,\"waveComplete\":false}",
          "byte_length": 369,
          "base64": "eyJ3YXZlS2V5Ijoid2F2ZS1kZW1vLTAxIiwidGFza0lkIjoidGFzay1kZW1vLTAxIiwib3V0Y29tZSI6InN1Y2NlZWRlZCIsImV2aWRlbmNlIjoie1wia2luZFwiOlwidGFzbGltLm1hbnVzLnRhc2tfZGV0YWlsX3Rlcm1pbmFsX3JlY2VpcHQudjFcIixcInRhc2tfaWRcIjpcIkFiQ2RFZjAxMjM0NTY3ODlHaElqS2xcIixcImFnZW50X3N0YXR1c1wiOlwic3RvcHBlZFwiLFwic3RvcF9yZWFzb25cIjpcImZpbmlzaFwiLFwiaGFzX3J1bm5pbmdfYmFja2dyb3VuZF9qb2JzXCI6ZmFsc2UsXCJldmVudF9pZFwiOlwiZXZ0OnYyLTAwMVwifSIsImNoZWNrcyI6e30sInJlcXVpcmVzSHVtYW5EZWNpc2lvbiI6ZmFsc2UsIndhdmVDb21wbGV0ZSI6ZmFsc2V9",
          "sha256": "cd06b69531d1430f892560238db7bddf2dcc9b65b0a14d6c8cc0b70298a31279"
        },
        "canonical": {
          "encoding": "ASCII (identical bytes to UTF-8)",
          "line_ending": "LF only",
          "trailing_lf": true,
          "byte_length": 166,
          "text": "AUTOPILOT-EVENT-V2\nPOST\n/api/autopilot/events\n1760000000\nmanus-bridge\nevt:v2-001\nwave.task.completed\ncd06b69531d1430f892560238db7bddf2dcc9b65b0a14d6c8cc0b70298a31279\n",
          "base64": "QVVUT1BJTE9ULUVWRU5ULVYyClBPU1QKL2FwaS9hdXRvcGlsb3QvZXZlbnRzCjE3NjAwMDAwMDAKbWFudXMtYnJpZGdlCmV2dDp2Mi0wMDEKd2F2ZS50YXNrLmNvbXBsZXRlZApjZDA2YjY5NTMxZDE0MzBmODkyNTYwMjM4ZGI3YmRkZjJkY2M5YjY1YjBhMTRkNmM4Y2MwYjcwMjk4YTMxMjc5Cg=="
        },
        "test_only_key": {
          "encoding": "ASCII/UTF-8 exact bytes",
          "text": "bridge-v2-golden-secret-not-for-use",
          "base64": "YnJpZGdlLXYyLWdvbGRlbi1zZWNyZXQtbm90LWZvci11c2U=",
          "not_for_production": true
        },
        "hmac_sha256_hex_lowercase": "ffb9595c31c404602bcbe2bac7e9729c7947a896eaa7ac4125ae95abd6551e65",
        "signature_header_value": "v2=ffb9595c31c404602bcbe2bac7e9729c7947a896eaa7ac4125ae95abd6551e65"
      }
    """;

    [Fact]
    public void Sole_proposed_integration_acceptance_vector_pins_exact_body_canonical_bytes_and_hmac()
    {
        using var document = JsonDocument.Parse(OwnerGoldenVectorJson);
        var root = document.RootElement;
        var values = root.GetProperty("values");
        var headers = root.GetProperty("headers");
        var rawBodyVector = root.GetProperty("raw_body");
        var canonicalVector = root.GetProperty("canonical");
        var keyVector = root.GetProperty("test_only_key");

        Assert.Equal("PROPOSED_UNAPPROVED_TEST_VECTOR", ReadString(root, "status"));
        Assert.True(keyVector.GetProperty("not_for_production").GetBoolean());
        Assert.Equal("v2", ReadString(values, "version"));
        Assert.Equal("application/json", ReadString(headers, "Content-Type"));
        Assert.Equal(ReadString(values, "timestamp"), ReadString(headers, "X-Autopilot-Timestamp"));
        Assert.Equal(ReadString(values, "source"), ReadString(headers, "X-Autopilot-Source"));
        Assert.Equal(ReadString(values, "event_id"), ReadString(headers, "X-Autopilot-Event-Id"));
        Assert.Equal(ReadString(values, "event_type"), ReadString(headers, "X-Autopilot-Event-Type"));
        Assert.Equal("v2", ReadString(headers, "X-Autopilot-Signature-Version"));

        var rawBody = Convert.FromBase64String(ReadString(rawBodyVector, "base64"));
        Assert.Equal(369, rawBodyVector.GetProperty("byte_length").GetInt32());
        Assert.Equal(369, rawBody.Length);
        Assert.Equal(ReadString(rawBodyVector, "utf8"), Encoding.UTF8.GetString(rawBody));
        var bodyDigest = Convert.ToHexString(SHA256.HashData(rawBody)).ToLowerInvariant();
        Assert.Equal(ReadString(rawBodyVector, "sha256"), bodyDigest);
        Assert.Equal("cd06b69531d1430f892560238db7bddf2dcc9b65b0a14d6c8cc0b70298a31279", bodyDigest);

        var canonicalText = ReadString(canonicalVector, "text");
        var canonicalBytes = Convert.FromBase64String(ReadString(canonicalVector, "base64"));
        Assert.Equal("ASCII (identical bytes to UTF-8)", ReadString(canonicalVector, "encoding"));
        Assert.Equal("LF only", ReadString(canonicalVector, "line_ending"));
        Assert.True(canonicalVector.GetProperty("trailing_lf").GetBoolean());
        Assert.Equal(166, canonicalVector.GetProperty("byte_length").GetInt32());
        Assert.Equal(166, canonicalBytes.Length);
        Assert.Equal(canonicalText, Encoding.ASCII.GetString(canonicalBytes));
        Assert.EndsWith("\n", canonicalText);
        Assert.DoesNotContain((byte)'\r', canonicalBytes);
        Assert.Equal(canonicalBytes, Encoding.ASCII.GetBytes(canonicalText));

        var key = Convert.FromBase64String(ReadString(keyVector, "base64"));
        Assert.Equal(ReadString(keyVector, "text"), Encoding.ASCII.GetString(key));
        var expectedHmac = ReadString(root, "hmac_sha256_hex_lowercase");
        var actualHmac = Convert.ToHexString(HMACSHA256.HashData(key, canonicalBytes)).ToLowerInvariant();
        Assert.Equal("ffb9595c31c404602bcbe2bac7e9729c7947a896eaa7ac4125ae95abd6551e65", expectedHmac);
        Assert.Equal(expectedHmac, actualHmac);
        Assert.Equal($"v2={expectedHmac}", ReadString(root, "signature_header_value"));
        Assert.Equal(ReadString(root, "signature_header_value"), ReadString(headers, "X-Autopilot-Signature"));
    }

    [Fact]
    public void Legacy_experimental_not_accepted_helper_and_hmac_cannot_reproduce_the_proposed_vector()
    {
        using var document = JsonDocument.Parse(OwnerGoldenVectorJson);
        var root = document.RootElement;
        var values = root.GetProperty("values");
        var rawBody = Convert.FromBase64String(ReadString(root.GetProperty("raw_body"), "base64"));
        var acceptedCanonical = Convert.FromBase64String(ReadString(root.GetProperty("canonical"), "base64"));
        var key = Convert.FromBase64String(ReadString(root.GetProperty("test_only_key"), "base64"));

        // Reuse the proposed vector's exact metadata, body, and synthetic key so
        // this isolates the legacy canonicalization difference, not key choice.
        var legacyCanonical = AutopilotLegacyExperimentalNotAcceptedV2OfflineParityTests.LegacyExperimentalNotAcceptedSigningInput(
            ReadString(values, "timestamp"),
            ReadString(values, "source"),
            ReadString(values, "event_type"),
            ReadString(values, "event_id"),
            rawBody);
        const string expectedLegacyCanonicalBase64 =
            "dGFzbGltLWF1dG9waWxvdC12MgoxNzYwMDAwMDAwCjEyOm1hbnVzLWJyaWRnZQoxOTp3YXZlLnRhc2suY29tcGxldGVkCjEwOmV2dDp2Mi0wMDEKY2QwNmI2OTUzMWQxNDMwZjg5MjU2MDIzOGRiN2JkZGYyZGNjOWI2NWIwYTE0ZDZjOGNjMGI3MDI5OGEzMTI3OQo=";
        const string expectedLegacyHmac = "d853cd6e8a867d8a2e3beff54606dc6f444eb661ad8665342f445eb29f7fe4cd";

        Assert.Equal(166, acceptedCanonical.Length);
        Assert.Equal(149, legacyCanonical.Length);
        Assert.Equal(expectedLegacyCanonicalBase64, Convert.ToBase64String(legacyCanonical));
        Assert.NotEqual(Convert.ToBase64String(acceptedCanonical), Convert.ToBase64String(legacyCanonical));

        var acceptedHmac = HMACSHA256.HashData(key, acceptedCanonical);
        var legacyHmac = HMACSHA256.HashData(key, legacyCanonical);
        Assert.Equal("ffb9595c31c404602bcbe2bac7e9729c7947a896eaa7ac4125ae95abd6551e65",
            Convert.ToHexString(acceptedHmac).ToLowerInvariant());
        Assert.Equal(expectedLegacyHmac, Convert.ToHexString(legacyHmac).ToLowerInvariant());
        Assert.NotEqual(Convert.ToHexString(acceptedHmac), Convert.ToHexString(legacyHmac));
        Assert.False(CryptographicOperations.FixedTimeEquals(acceptedHmac, legacyHmac));
    }

    // Expected canonical Base64 and HMAC values were independently recomputed
    // from the sole proposed owner-supplied fixture with its synthetic key.
    public static object[][] SoleProposedIntegrationAcceptanceVectorMutationCases =>
        new object[][]
        {
            new object[]
            {
                "source", "alternate-source",
                "QVVUT1BJTE9ULUVWRU5ULVYyClBPU1QKL2FwaS9hdXRvcGlsb3QvZXZlbnRzCjE3NjAwMDAwMDAKYWx0ZXJuYXRlLXNvdXJjZQpldnQ6djItMDAxCndhdmUudGFzay5jb21wbGV0ZWQKY2QwNmI2OTUzMWQxNDMwZjg5MjU2MDIzOGRiN2JkZGYyZGNjOWI2NWIwYTE0ZDZjOGNjMGI3MDI5OGEzMTI3OQo=",
                "63a6fa6845b442d04c7cc69aa892cb962b4a2d673fac04ef00766a4eccef5eff",
                369, "cd06b69531d1430f892560238db7bddf2dcc9b65b0a14d6c8cc0b70298a31279"
            },
            new object[]
            {
                "event-type", "wave.task.completedX",
                "QVVUT1BJTE9ULUVWRU5ULVYyClBPU1QKL2FwaS9hdXRvcGlsb3QvZXZlbnRzCjE3NjAwMDAwMDAKbWFudXMtYnJpZGdlCmV2dDp2Mi0wMDEKd2F2ZS50YXNrLmNvbXBsZXRlZFgKY2QwNmI2OTUzMWQxNDMwZjg5MjU2MDIzOGRiN2JkZGYyZGNjOWI2NWIwYTE0ZDZjOGNjMGI3MDI5OGEzMTI3OQo=",
                "70109dcc4df4880963c33cdaaa07727409681b928e5e29b8f7a48e123a12257d",
                369, "cd06b69531d1430f892560238db7bddf2dcc9b65b0a14d6c8cc0b70298a31279"
            },
            new object[]
            {
                "external-event-id", "evt:v2-002",
                "QVVUT1BJTE9ULUVWRU5ULVYyClBPU1QKL2FwaS9hdXRvcGlsb3QvZXZlbnRzCjE3NjAwMDAwMDAKbWFudXMtYnJpZGdlCmV2dDp2Mi0wMDIKd2F2ZS50YXNrLmNvbXBsZXRlZApjZDA2YjY5NTMxZDE0MzBmODkyNTYwMjM4ZGI3YmRkZjJkY2M5YjY1YjBhMTRkNmM4Y2MwYjcwMjk4YTMxMjc5Cg==",
                "295b2678643f4ea0e4a5c138bfecb37517f5eff9411adba03c266732aec09864",
                369, "cd06b69531d1430f892560238db7bddf2dcc9b65b0a14d6c8cc0b70298a31279"
            },
            new object[]
            {
                "timestamp", "1760000001",
                "QVVUT1BJTE9ULUVWRU5ULVYyClBPU1QKL2FwaS9hdXRvcGlsb3QvZXZlbnRzCjE3NjAwMDAwMDEKbWFudXMtYnJpZGdlCmV2dDp2Mi0wMDEKd2F2ZS50YXNrLmNvbXBsZXRlZApjZDA2YjY5NTMxZDE0MzBmODkyNTYwMjM4ZGI3YmRkZjJkY2M5YjY1YjBhMTRkNmM4Y2MwYjcwMjk4YTMxMjc5Cg==",
                "c299937a138b9becdd8199be7b9c248a879e76fc0a664e456b945d965024a57a",
                369, "cd06b69531d1430f892560238db7bddf2dcc9b65b0a14d6c8cc0b70298a31279"
            },
            new object[]
            {
                "raw-body", "append-ascii-space",
                "QVVUT1BJTE9ULUVWRU5ULVYyClBPU1QKL2FwaS9hdXRvcGlsb3QvZXZlbnRzCjE3NjAwMDAwMDAKbWFudXMtYnJpZGdlCmV2dDp2Mi0wMDEKd2F2ZS50YXNrLmNvbXBsZXRlZAozYWQ2NDIzYWY1M2MxMDY3OTAyNDM0MjMxZTJjZGMxOGQ1MGM0YzdmNWFlNmM3ZDY1MmNkNWRhNGVjYWYxZWNjCg==",
                "f7651424819c0784c361e29aac6c6dd2ece2438218eb0c985108cd0f6e93e34b",
                370, "3ad6423af53c1067902434231e2cdc18d50c4c7f5ae6c7d652cd5da4ecaf1ecc"
            }
        };

    [Theory]
    [MemberData(nameof(SoleProposedIntegrationAcceptanceVectorMutationCases))]
    public void Each_sole_proposed_integration_vector_mutation_changes_pinned_canonical_bytes_and_rejects_original_hmac(
        string field,
        string mutatedValue,
        string expectedCanonicalBase64,
        string expectedHmac,
        int expectedRawBodyLength,
        string expectedRawBodySha256)
    {
        using var document = JsonDocument.Parse(OwnerGoldenVectorJson);
        var root = document.RootElement;
        var values = root.GetProperty("values");
        var rawBody = Convert.FromBase64String(ReadString(root.GetProperty("raw_body"), "base64"));
        var canonicalText = ReadString(root.GetProperty("canonical"), "text");
        var originalCanonical = Convert.FromBase64String(ReadString(root.GetProperty("canonical"), "base64"));
        var key = Convert.FromBase64String(ReadString(root.GetProperty("test_only_key"), "base64"));
        var originalHmacHex = ReadString(root, "hmac_sha256_hex_lowercase");
        var originalHmac = Convert.FromHexString(originalHmacHex);

        var mutatedRawBody = rawBody;
        var mutatedCanonicalText = field switch
        {
            "timestamp" => ReplaceCanonicalLine(canonicalText, 3, mutatedValue),
            "source" => ReplaceCanonicalLine(canonicalText, 4, mutatedValue),
            "external-event-id" => ReplaceCanonicalLine(canonicalText, 5, mutatedValue),
            "event-type" => ReplaceCanonicalLine(canonicalText, 6, mutatedValue),
            "raw-body" => MutateBodyAndCanonicalHash(),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown vector mutation.")
        };

        int rawBodyLength = mutatedRawBody.Length;
        var rawBodySha256 = Convert.ToHexString(SHA256.HashData(mutatedRawBody)).ToLowerInvariant();
        Assert.Equal(expectedRawBodyLength, rawBodyLength);
        Assert.Equal(expectedRawBodySha256, rawBodySha256);

        var mutatedCanonical = Encoding.ASCII.GetBytes(mutatedCanonicalText);
        var mutatedCanonicalBase64 = Convert.ToBase64String(mutatedCanonical);
        Assert.Equal(expectedCanonicalBase64, mutatedCanonicalBase64);
        Assert.NotEqual(Convert.ToBase64String(originalCanonical), mutatedCanonicalBase64);

        var mutatedHmac = HMACSHA256.HashData(key, mutatedCanonical);
        var mutatedHmacHex = Convert.ToHexString(mutatedHmac).ToLowerInvariant();
        Assert.Equal(expectedHmac, mutatedHmacHex);
        Assert.NotEqual(originalHmacHex, mutatedHmacHex);
        Assert.False(CryptographicOperations.FixedTimeEquals(originalHmac, mutatedHmac));

        string MutateBodyAndCanonicalHash()
        {
            Assert.Equal("append-ascii-space", mutatedValue);
            mutatedRawBody = new byte[rawBody.Length + 1];
            Buffer.BlockCopy(rawBody, 0, mutatedRawBody, 0, rawBody.Length);
            mutatedRawBody[^1] = (byte)' ';
            var changedDigest = Convert.ToHexString(SHA256.HashData(mutatedRawBody)).ToLowerInvariant();
            return ReplaceCanonicalLine(canonicalText, 7, changedDigest);
        }
    }

    private static string ReplaceCanonicalLine(string canonicalText, int lineIndex, string replacement)
    {
        var lines = canonicalText.Split('\n');
        Assert.Equal(9, lines.Length);
        Assert.Equal(string.Empty, lines[^1]);
        lines[lineIndex] = replacement;
        return string.Join("\n", lines);
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString() ?? throw new InvalidOperationException($"Missing string property '{propertyName}'.");
}

// Each request/context owns its SQLite connection. Sharing one opened connection
// lets request-scope cleanup race EF's per-connection function registration.
public sealed class AutopilotAcknowledgementFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-ack-{Guid.NewGuid():N}.sqlite");
    public AcknowledgementProviderProbe ProviderProbe { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Autopilot:WatchdogEnabled"] = "false" }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite($"Data Source={databasePath};Default Timeout=30"));
            services.RemoveAll<IWaveLaunchProvider>();
            services.AddSingleton<IWaveLaunchProvider>(ProviderProbe);
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

public sealed class AcknowledgementProviderProbe : IWaveLaunchProvider
{
    private int providerCalls;

    public string ProviderKey => "acknowledgement-test-probe";
    public bool IsConfigured => true;
    public int ProviderCalls => Volatile.Read(ref providerCalls);

    public Task<WaveLaunchProviderResult> LaunchTaskAsync(
        WaveLaunchProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref providerCalls);
        return Task.FromResult(WaveLaunchProviderResult.Unavailable("unexpected_acknowledgement_test_provider_call"));
    }

    public Task<WaveLaunchTaskState?> GetTaskStateAsync(
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref providerCalls);
        return Task.FromResult<WaveLaunchTaskState?>(null);
    }
}
