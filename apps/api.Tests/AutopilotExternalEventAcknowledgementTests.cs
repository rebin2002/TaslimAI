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

public sealed class AutopilotAcceptedV2GoldenVectorFixtureTests
{
    // Copied as machine-readable data from the owner-supplied JSON vector
    // (SHA-256 a6197a2665c7310f7b58511005961710327791279a13a1efcf90c6bc1898a020).
    // This pure fixture test does not call or imply acceptance by the v2 HTTP receiver.
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
    public void Owner_supplied_v2_vector_pins_exact_body_canonical_bytes_hmac_and_metadata_mutation()
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

        // Mutating only the signed source metadata while retaining the body and
        // original signature must invalidate this pure fixture verification.
        var sourceMarker = $"\n{ReadString(values, "source")}\n";
        var sourceMarkerIndex = canonicalText.IndexOf(sourceMarker, StringComparison.Ordinal);
        Assert.True(sourceMarkerIndex >= 0);
        Assert.Equal(sourceMarkerIndex, canonicalText.LastIndexOf(sourceMarker, StringComparison.Ordinal));
        var mutatedCanonical = canonicalText.Replace(sourceMarker, "\nalternate-source\n", StringComparison.Ordinal);
        var mutatedHmac = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(mutatedCanonical))).ToLowerInvariant();
        Assert.NotEqual(expectedHmac, mutatedHmac);
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
