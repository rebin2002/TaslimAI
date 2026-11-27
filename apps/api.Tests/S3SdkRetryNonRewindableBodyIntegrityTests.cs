using System.Net;
using System.Net.Http;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Xunit;
using Xunit.Abstractions;
using FileOptions = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

/// <summary>
/// Test-only answer to the open scope-audit note on PR #680 (issue #459 lane):
/// "a PUT that the AWS SDK retries with a non-rewindable body can surface an exception
/// class the current ClassifyFailure ladder may map to unknown - nothing proved what the
/// adapter does in that case."
///
/// This suite drives the real pipeline - the real <see cref="AmazonS3Client"/> (AWSSDK
/// v4), the real <see cref="AwsS3CompatibleObjectClient"/> request shape and the real
/// <see cref="S3CompatibleFileStorageService"/> adapter - and replaces only the transport
/// inside the SDK's own public <see cref="Amazon.Runtime.HttpClientFactory"/> seam with a
/// deterministic in-process script. No socket is opened, no live R2/S3 provider or bucket
/// is contacted, no credential is read, and every endpoint/bucket/key value is a
/// synthetic non-resolvable literal. The script behaves like a maximally lenient
/// S3-compatible provider: the first PUT attempt has <c>N</c> body bytes consumed and the
/// connection closed with no HTTP response (the incident class of #459); every later
/// attempt is committed with HTTP 200 + ETag for whatever body bytes actually arrive, so
/// any rewind-less partial replay would be silently published as if complete.
///
/// Empirical contracts pinned (deterministic on the pinned AWSSDK.S3 4.0.103.x):
/// 1. A PUT with the production non-seekable <c>CountingReadStream</c> body and
///    <c>AutoResetStreamPosition = false</c> is never re-attempted by the SDK after a
///    no-response abort: the transport-retry rewindability gate rejects it, so no short
///    body can reach a lenient provider (no partial object publication).
/// 2. The call surfaces as an exception whose innermost cause is the transport
///    <see cref="IOException"/>; through the adapter it terminates as the fail-closed
///    <see cref="FileStorageOperationException"/> contract and never completes normally
///    (no false upload success).
/// 3. Control: with an otherwise identical request but a seekable body, the same script
///    DOES retry - the SDK rewinds and re-sends the full declared body before committing.
///    Rewindability alone is the differentiator, which is exactly what distinguishes this
///    reproduction from every provider-answering or handcrafted-signature lane.
///
/// Distinct lanes kept out of scope: the failure-class taxonomy of the surfaced exception
/// (PR #1175 pins the <c>unknown</c> fallback, PR #680 owns the diagnostic ladder), the
/// Chat-boundary 503 contracts for the handcrafted incident signatures (PRs #1134/#1140),
/// committed-but-unacknowledged user-retry de-duplication at the upload endpoint
/// (PRs #768/#797), and <c>CountingReadStream</c> byte-length/hash QA (issue #689). This
/// file asserts no <c>FailureClass=</c> string, names no HTTP status contract, and
/// touches no production file.
/// </summary>
public sealed class S3SdkRetryNonRewindableBodyIntegrityTests
{
    // Synthetic, non-resolvable values. The endpoint is never resolved: the scripted
    // factory intercepts every send in-process before any socket exists. HTTPS posture is
    // kept because the production DisablePayloadSigning request (CreatePutObjectRequest)
    // requires it at signing time.
    private const string AccessKey = "AKIASELFSYNTHETIC00001";
    private const string SecretKey = "synthetic-secret-never-sent-never-read";
    private const string Bucket = "synthetic-private-bucket";
    private const string StorageKey = "00000000000000000000000000000000/11111111111111111111111111111111/research.pdf";
    private const int BodyLength = 128 * 1024;
    private const int AbortAfterBytes = 32 * 1024;

    private readonly ITestOutputHelper output;

    public S3SdkRetryNonRewindableBodyIntegrityTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Mid_body_abort_with_non_rewindable_body_is_never_replayed_and_fails_closed_without_partial_publication()
    {
        // The provider consumes a quarter of the declared body, then closes without an
        // HTTP response, leaving the non-seekable CountingReadStream provably positioned
        // mid-body. A rewind-less SDK retry would replay a short body into the lenient
        // commit path below - the worst case this lane must rule out.
        var transport = new ScriptedTransport(abortAfterBytes: AbortAfterBytes);
        using var fixture = new AdapterFixture(transport);
        await using var body = ProductionPdfBody();

        var outcome = await fixture.StoreAsync(body);

        Describe("mid-body", transport, outcome);

        // (1) SDK mechanics: the transport-retry gate refuses to replay a body it cannot
        // rewind, so exactly one wire attempt exists and the committed path was never
        // reached with a short body.
        Assert.Equal(1, transport.Attempts.Count);
        Assert.DoesNotContain(transport.Attempts, attempt =>
            attempt.Committed && attempt.BodyBytes < BodyLength);

        // (2) Fail-closed, no false success: the adapter never completes the store.
        Assert.False(outcome.Completed,
            "StoreAsync reported success after a mid-body abort with a non-rewindable body - a false upload success.");
        Assert.NotNull(outcome.Failure);
        Assert.IsType<FileStorageOperationException>(FindInChain(outcome.Failure!));

        // Reproduction validity: the stream really was consumed mid-body when the abort
        // surfaced, i.e. the retry decision was evaluated against a partially sent body.
        Assert.InRange(transport.Attempts[0].BodyBytes, 1, BodyLength - 1);
    }

    [Fact]
    public async Task Lost_response_abort_with_non_rewindable_body_fails_closed_without_false_success()
    {
        // Incident-shaped variant: the provider closes without consuming (from the
        // listener's perspective) any body byte and without responding - matching the
        // recorded #459 signature where the client considered the PUT fully handed off.
        var transport = new ScriptedTransport(abortAfterBytes: 0);
        using var fixture = new AdapterFixture(transport);
        await using var body = ProductionPdfBody();

        var outcome = await fixture.StoreAsync(body);

        Describe("lost-response", transport, outcome);

        Assert.Single(transport.Attempts);
        Assert.DoesNotContain(transport.Attempts, attempt =>
            attempt.Committed && attempt.BodyBytes < BodyLength);
        Assert.False(outcome.Completed,
            "StoreAsync reported success after a no-response abort with a non-rewindable body - a false upload success.");
        Assert.IsType<FileStorageOperationException>(FindInChain(outcome.Failure!));
    }

    [Fact]
    public async Task Surfaced_sdk_failure_shape_is_a_transport_abort_not_a_provider_response()
    {
        // Documents the shape that reaches the adapter's diagnostics for #680's open note:
        // an aborted connection whose cause is the raw transport IOException - no HTTP
        // status, no provider error code, no request id. The failure-class mapping of
        // this shape is #1175/#680's lane; this test pins only that it IS a no-response
        // transport failure and that no success response can accompany it.
        var transport = new ScriptedTransport(abortAfterBytes: AbortAfterBytes);
        using var fixture = new AdapterFixture(transport);
        await using var body = ProductionPdfBody();

        var raw = await fixture.StoreAsyncRawSdkOnly(body);

        Assert.NotNull(raw);
        Assert.DoesNotContain(raw!, chain => chain is AmazonServiceException answered && (int)answered.StatusCode != 0);

        var chain = raw!;
        Assert.IsType<IOException>(chain[^1]);
        Assert.DoesNotContain(transport.Attempts, attempt => attempt.Committed);
    }

    [Fact]
    public async Task Seekable_control_same_abort_is_replayed_from_position_zero_with_the_full_declared_body()
    {
        // Control that isolates the differentiator: with the identical scripted abort and
        // an otherwise identical request, a rewindable (seekable) body IS retried - the
        // SDK rewinds it and the lenient provider receives the complete declared byte
        // count before committing. Proves the script is reachable, the retry machinery is
        // alive, and only non-rewindability suppressed the replay in the lane tests.
        var transport = new ScriptedTransport(abortAfterBytes: AbortAfterBytes);
        using var fixture = new AdapterFixture(transport);
        await using var body = new MemoryStream(SyntheticPdf(), writable: false);

        var outcome = await fixture.StoreAsync(body);

        Describe("seekable control", transport, outcome);

        Assert.Equal(2, transport.Attempts.Count);
        Assert.False(transport.Attempts[0].Committed);
        Assert.True(transport.Attempts[1].Committed);
        Assert.Equal(BodyLength, transport.Attempts[1].BodyBytes);
        Assert.True(outcome.Completed,
            "the seekable control must complete: it re-sent the full declared body and was committed");

        // Publication integrity even on the success path: what the provider committed is
        // exactly the complete declared body, never a fragment.
        Assert.Equal(SyntheticPdf(), transport.Attempts[1].Content);
    }

    private static CountingReadStream ProductionPdfBody() =>
        // Exactly the Chat PDF upload shape of FileProcessingService: a non-seekable
        // CountingReadStream carrying the validated declared size, which
        // CreatePutObjectRequest turns into an explicit Content-Length with
        // AutoResetStreamPosition = false and UseChunkEncoding = false.
        new(SyntheticPdfStream(), long.MaxValue, BodyLength);

    private static MemoryStream SyntheticPdfStream() => new(SyntheticPdf(), writable: false);

    private static byte[] SyntheticPdf()
    {
        var bytes = new byte[BodyLength];
        Encoding.UTF8.GetBytes("%PDF-1.7\n").CopyTo(bytes, 0);
        for (var i = 9; i < BodyLength; i++)
            bytes[i] = (byte)('A' + (i % 26));
        return bytes;
    }

    private static Exception FindInChain(Exception outer)
    {
        for (Exception? current = outer; current is not null; current = current.InnerException)
            if (current is FileStorageOperationException mapped)
                return mapped;
        return outer;
    }

    private void Describe(string scenario, ScriptedTransport transport, AdapterFixture.StoreResult outcome)
    {
        output.WriteLine($"[{scenario}] attempts=[" +
            string.Join(", ", transport.Attempts.Select(a => $"bytes={a.BodyBytes};committed={a.Committed}")) +
            $"] completed={outcome.Completed} sourceConsumed={transport.SourceBytesConsumed}");
        if (outcome.Failure is { } failure)
        {
            var chain = new StringBuilder();
            for (Exception? c = failure; c is not null; c = c.InnerException)
            {
                chain.Append(c.GetType().Name);
                chain.Append(": ");
                chain.Append(c.Message.Length > 90 ? c.Message[..90] : c.Message);
                chain.Append(" <- ");
            }
            if (chain.Length >= 4) chain.Length -= 4;
            output.WriteLine($"[{scenario}] chain: {chain}");
        }
    }

    /// <summary>
    /// The production adapter stack: <see cref="S3CompatibleFileStorageService"/> -&gt;
    /// <see cref="AwsS3CompatibleObjectClient"/> (real <c>CreatePutObjectRequest</c>
    /// shape) -&gt; real <see cref="AmazonS3Client"/> whose SDK-level transport is
    /// scripted. HTTPS endpoint posture is synthetic and never resolved.
    /// </summary>
    private sealed class AdapterFixture : IDisposable
    {
        private readonly AmazonS3Client s3;
        private readonly S3CompatibleFileStorageService service;

        public AdapterFixture(ScriptedTransport transport)
        {
            var config = new AmazonS3Config
            {
                ServiceURL = "https://synthetic.invalid",
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                // Mirror the production R2-compatible posture; only the SDK-internal
                // transport is scripted.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                HttpClientFactory = new ScriptedHttpClientFactory(transport),
                // Each test must own its scripted transport; never share a cached
                // HttpClient across fixtures.
                CacheHttpClient = false,
            };
            s3 = new AmazonS3Client(AccessKey, SecretKey, config);
            service = new S3CompatibleFileStorageService(
                new AwsS3CompatibleObjectClient(s3),
                Options.Create(new FileOptions
                {
                    StorageProvider = FileStorageProviders.S3Compatible,
                    S3Endpoint = "https://synthetic.invalid",
                    S3Region = "us-east-1",
                    S3Bucket = Bucket,
                    S3AccessKey = AccessKey,
                    S3SecretKey = SecretKey,
                }),
                NullLogger<S3CompatibleFileStorageService>.Instance);
        }

        public readonly record struct StoreResult(Exception? Failure, bool Completed);

        public async Task<StoreResult> StoreAsync(Stream body)
        {
            try
            {
                await service.StoreAsync(StorageKey, body, CancellationToken.None);
                return new StoreResult(null, true);
            }
            catch (Exception failure)
            {
                return new StoreResult(failure, false);
            }
        }

        /// <summary>
        /// Bypasses the adapter's wrapping (main currently does not preserve the cause)
        /// to expose the raw SDK exception chain for shape documentation.
        /// </summary>
        public async Task<IReadOnlyList<Exception>?> StoreAsyncRawSdkOnly(Stream body)
        {
            try
            {
                var client = new AwsS3CompatibleObjectClient(s3);
                await client.PutAsync(Bucket, StorageKey, body, CancellationToken.None);
                return null;
            }
            catch (Exception failure)
            {
                var chain = new List<Exception>();
                for (Exception? c = failure; c is not null; c = c.InnerException) chain.Add(c);
                return chain;
            }
        }

        public void Dispose()
        {
            service.Dispose();
            s3.Dispose();
        }
    }

    private sealed class ScriptedHttpClientFactory(ScriptedTransport transport) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(transport);
    }

    /// <summary>
    /// Maximally lenient scripted provider: attempt 1 consumes the configured body
    /// prefix then dies exactly like a connection reset behind .NET's HTTP stack
    /// (<see cref="IOException"/> wrapped in <see cref="HttpRequestException"/>); later
    /// attempts commit (200 + ETag) whatever body bytes arrive. Records the exact content
    /// received per attempt so partial publication is directly observable.
    /// </summary>
    private sealed class ScriptedTransport(int abortAfterBytes) : HttpMessageHandler
    {
        public sealed record Attempt(int BodyBytes, bool Committed, byte[] Content);

        public List<Attempt> Attempts { get; } = new();
        public long SourceBytesConsumed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new MemoryStream();
            var first = Attempts.Count == 0;
            var cap = first ? abortAfterBytes : int.MaxValue;
            if (request.Content is not null)
            {
                await using var source = await request.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[16 * 1024];
                while (content.Length < cap)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, cap - (int)content.Length)), cancellationToken);
                    if (read == 0) break;
                    content.Write(buffer, 0, read);
                }

                SourceBytesConsumed += content.Length;
            }

            if (first)
            {
                Attempts.Add(new Attempt((int)content.Length, false, content.ToArray()));
                // Exactly what SocketsHttpHandler surfaces for a mid-request reset on
                // .NET 8: HttpRequestException whose cause is an IOException.
                throw new HttpRequestException(
                    "scripted loopback: connection was aborted without an HTTP response",
                    new IOException("Unable to read data from the transport connection: connection reset by peer."));
            }

            Attempts.Add(new Attempt((int)content.Length, true, content.ToArray()));
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("ETag", "\"synthetic-commit\"");
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            return response;
        }
    }
}
