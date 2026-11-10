using System.Net;
using System.Text;
using Taslim.Api.Autopilot;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free, intentionally RED acceptance for TaslimAI #718's existing
/// response-boundary owner. Do not turn it green by accepting truncated JSON:
/// a Bridge response exceeding the limit is never a task-creation ACK or a
/// terminal-state receipt. No real Bridge, Manus, or production endpoint is used.
/// </summary>
public sealed class ManusBridgeResponseBoundaryContractTests
{
    private const string TestTokenEnvironment = "TASLIM_OFFLINE_BOUNDED_BRIDGE_FAKE_TOKEN";
    private const string FakeTaskId = "ABCDEFGHIJKLMNOPQRSTUV";
    private const int ReadBudget = 131_072;

    [Fact]
    public async Task Oversized_declared_length_create_response_with_parseable_json_prefix_is_not_a_launch_ack()
    {
        var previous = Environment.GetEnvironmentVariable(TestTokenEnvironment);
        Environment.SetEnvironmentVariable(TestTokenEnvironment, "provider-free-test-token");
        try
        {
            var payload = "{\"ok\":true,\"task_id\":\"" + FakeTaskId + "\"}" + new string(' ', 160_000);
            using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
            using var http = new HttpClient(handler);
            var provider = NewProvider(http);
            var request = new WaveLaunchProviderRequest(
                "wave-response-boundary", "task-response-boundary", "Offline test",
                "Do not call external providers", new string('a', 40),
                "feature/offline", "offline-response-boundary-idempotency")
            {
                FencingToken = 1
            };

            var result = await provider.LaunchTaskAsync(request);

            // A previously valid prefix followed by excess whitespace must not
            // be treated as proof that Manus created a task.
            Assert.Equal(WaveLaunchProviderOutcomes.Unavailable, result.Outcome);
            Assert.False(result.IsLaunched);
            Assert.Null(result.ExternalTaskId);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestTokenEnvironment, previous);
        }
    }

    [Fact]
    public async Task Oversized_unknown_length_task_state_must_stop_reading_before_stream_budget()
    {
        var previous = Environment.GetEnvironmentVariable(TestTokenEnvironment);
        Environment.SetEnvironmentVariable(TestTokenEnvironment, "provider-free-test-token");
        try
        {
            var payload = Encoding.UTF8.GetBytes("{\"status\":\"stopped\"}" + new string(' ', 200_000));
            using var body = new ReadObservingStream(payload);
            using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            });
            using var http = new HttpClient(handler);
            var provider = NewProvider(http);

            // A streamed response with no trustworthy Content-Length must
            // never be accepted based on its valid/truncated JSON prefix.
            var state = await provider.GetTaskStateAsync(FakeTaskId);

            // The fake never enforces the cap. This assertion must fail if
            // production still buffers the entire unknown-length response.
            Assert.True(body.BytesRead <= ReadBudget + 1,
                $"Provider consumed {body.BytesRead} bytes; expected at most {ReadBudget + 1}");
            Assert.Null(state);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestTokenEnvironment, previous);
        }
    }

    [Fact]
    public async Task Small_unknown_length_task_state_remains_readable_and_normalized()
    {
        var previous = Environment.GetEnvironmentVariable(TestTokenEnvironment);
        Environment.SetEnvironmentVariable(TestTokenEnvironment, "provider-free-test-token");
        try
        {
            var payload = Encoding.UTF8.GetBytes("{\"status\":\"stopped\"}");
            using var body = new ReadObservingStream(payload);
            using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            });
            using var http = new HttpClient(handler);
            var provider = NewProvider(http);

            // A future bounded-reader fix must not pass the two negative
            // contracts by rejecting every otherwise valid small response.
            var state = await provider.GetTaskStateAsync(FakeTaskId);

            Assert.NotNull(state);
            Assert.Equal(WaveLaunchTaskStates.Cancelled, state!.Status);
            Assert.True(state.IsTerminal);
            Assert.False(state.IsSuccess);
            Assert.Equal(payload.Length, body.BytesRead);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestTokenEnvironment, previous);
        }
    }

    private static ManusBridgeWaveLaunchProvider NewProvider(HttpClient http)
    {
        var options = new AutopilotOptions
        {
            BridgeBaseUrl = "https://offline-bridge.invalid",
            BridgeTokenEnvironmentVariable = TestTokenEnvironment,
            BridgeTimeoutSeconds = 5
        };
        options.Normalize();
        return new ManusBridgeWaveLaunchProvider(http, options);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(respond(request));
        }
    }

    // Non-seekable, unknown-Content-Length fake that intentionally does NOT
    // enforce any read limit. The assertion above must detect buffering by
    // the actual provider, not by the fake's own IOException or clamping.
    private sealed class ReadObservingStream(byte[] data) : Stream
    {
        private readonly MemoryStream inner = new(data, writable: false);

        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
