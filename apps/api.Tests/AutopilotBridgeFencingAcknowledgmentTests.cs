using System.Net;
using System.Text;
using Taslim.Api.Autopilot;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free acceptance contract for the Bridge fencing-epoch acknowledgment.
/// Every response here is synthesized in-process: no Manus call, no deployed
/// Bridge, no database, and no paid provider call is made.
///
/// The positive case below is the exact-match half of the contract anchored by
/// PR #731: a 2xx create response is admitted only when the bridge echoes
/// <c>fencing_high_water_token</c> equal to the request epoch. Unlike the
/// PostgreSQL takeover case, this fixture needs no configured provider beyond a
/// fake in-process handler.
/// </summary>
public sealed class AutopilotBridgeFencingAcknowledgmentTests
{
    private const long RequestedFence = 2;

    [Fact]
    public async Task Exact_high_water_acknowledgment_admits_the_launch()
    {
        var body = $$"""{"task_id":"verified-fake-task","fencing_high_water_token":{{RequestedFence}}}""";
        var (result, requestCount) = await RunAsync(HttpStatusCode.OK, body);

        Assert.Equal(1, requestCount);
        Assert.True(result.IsLaunched);
        Assert.Equal(WaveLaunchProviderOutcomes.Launched, result.Outcome);
        Assert.Equal("verified-fake-task", result.ExternalTaskId);
        Assert.Null(result.ReasonCode);
    }

    /// <summary>
    /// The acknowledgment is compared exactly, so an epoch ahead of the request
    /// is just as unverifiable as one behind it: the bridge is describing a
    /// different lease generation than the caller holds.
    /// </summary>
    [Theory]
    [InlineData("""{"task_id":"fake-task-1"}""", "bridge_fencing_high_water_missing")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":1}""", "bridge_fencing_high_water_mismatch")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":3}""", "bridge_fencing_high_water_mismatch")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":0}""", "bridge_fencing_high_water_mismatch")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":null}""", "bridge_fencing_high_water_missing")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":false}""", "bridge_fencing_high_water_missing")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":"two"}""", "bridge_fencing_high_water_missing")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":"-2"}""", "bridge_fencing_high_water_missing")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":2.5}""", "bridge_fencing_high_water_missing")]
    public async Task Unverified_high_water_acknowledgment_is_never_admission_and_never_retries_create(
        string body,
        string expectedReasonCode)
    {
        var (result, requestCount) = await RunAsync(HttpStatusCode.OK, body);

        Assert.False(result.IsLaunched,
            "A task identity must not authorize admission unless the bridge acknowledges this request's exact fencing epoch.");
        Assert.Equal(1, requestCount);
        Assert.Equal(WaveLaunchProviderOutcomes.Rejected, result.Outcome);
        Assert.Equal(expectedReasonCode, result.ReasonCode);
        Assert.Null(result.ExternalTaskId);
    }

    [Theory]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":2}""")]
    [InlineData("""{"taskId":"fake-task-1","fencingHighWaterToken":2}""")]
    [InlineData("""{"task_id":"fake-task-1","fencing_high_water_token":"2"}""")]
    [InlineData("""{"id":"fake-task-1","fencing_high_water_token":" 2 "}""")]
    [InlineData("""{"task_id":"fake-task-1","data":{"fencing_high_water_token":2}}""")]
    [InlineData("""{"task_id":"fake-task-1","result":{"fencingHighWaterToken":2}}""")]
    public async Task Acknowledgment_in_a_recognized_response_shape_admits_the_launch(string body)
    {
        var (result, requestCount) = await RunAsync(HttpStatusCode.OK, body);

        Assert.Equal(1, requestCount);
        Assert.True(result.IsLaunched);
        Assert.Equal("fake-task-1", result.ExternalTaskId);
    }

    /// <summary>
    /// Precedence guard: the existing fail-closed behavior for a response with
    /// no identity at all stays "unavailable", even when an acknowledgment is
    /// present, because nothing was created to reconcile against.
    /// </summary>
    [Fact]
    public async Task Missing_identity_is_still_unavailable_rather_than_a_fencing_rejection()
    {
        var (result, requestCount) = await RunAsync(
            HttpStatusCode.OK,
            $$"""{"ok":true,"fencing_high_water_token":{{RequestedFence}}}""");

        Assert.Equal(1, requestCount);
        Assert.False(result.IsLaunched);
        Assert.Equal(WaveLaunchProviderOutcomes.Unavailable, result.Outcome);
        Assert.Equal("bridge_task_identity_missing", result.ReasonCode);
    }

    private static WaveLaunchProviderRequest CreateRequest(long fencingToken) =>
        new("replacement-wave", "replacement-task", "Replacement", "Fake-only contract test", new string('a', 40), "feature/replacement", "test-idempotency-key")
        {
            FencingToken = fencingToken,
        };

    /// <summary>
    /// Runs one launch against a synthetic response with a throwaway credential
    /// variable, so the test needs no configured provider and no real secret.
    /// </summary>
    private static async Task<(WaveLaunchProviderResult Result, int RequestCount)> RunAsync(HttpStatusCode status, string body)
    {
        var tokenVariable = $"TASLIM_TEST_BRIDGE_{Guid.NewGuid():N}";
        var priorTokenValue = Environment.GetEnvironmentVariable(tokenVariable);
        Environment.SetEnvironmentVariable(tokenVariable, "fake-only-test-credential");
        var handler = new FixedResponseHandler(status, body);
        try
        {
            using var http = new HttpClient(handler);
            var provider = new ManusBridgeWaveLaunchProvider(http, new AutopilotOptions
            {
                BridgeBaseUrl = "https://bridge.example.test",
                BridgeTokenEnvironmentVariable = tokenVariable,
                BridgeCreateTaskPath = "/v1/tasks",
                BridgeTimeoutSeconds = 5,
            });

            var result = await provider.LaunchTaskAsync(CreateRequest(RequestedFence));
            return (result, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, priorTokenValue);
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
