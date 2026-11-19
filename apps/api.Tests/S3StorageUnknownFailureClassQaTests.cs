using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free QA lane for the single S3/R2 no-response diagnostics class that
/// no open lane had pinned: the <c>unknown</c> fail-closed fallback.
///
/// The recognized categories are already owned elsewhere:
/// <c>network</c>/<c>tls</c>/<c>timeout</c>/<c>transport</c>/<c>s3_response</c>
/// and <c>s3_auth_or_permission</c> by <c>S3CompatibleStorageDiagnosticsTests</c>
/// (#680, stacked from #463/#465); <c>s3_no_http_response</c> (opaque status-0
/// shape) by #680 as well; and the user-visible status-0 transport boundary by
/// the #459 lanes #768 (upload endpoint) and #1140 (Chat turn).
///
/// <c>unknown</c> is what <see cref="Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure"/>
/// emits when the SDK surfaces a failure that carried no HTTP response and has no
/// recognized transport root cause - exactly the shape the AWS retry envelope
/// flagged in the #680 scope audit can produce. Nothing pinned that it stays
/// sanitized and still fails closed.
///
/// Synthetic in-process fixtures only: no R2/S3 endpoint, bucket or object, no
/// credentials, no customer attachment, no provider call, no deployment. No
/// shared test or production file was modified by this lane.
/// </summary>
public sealed class S3StorageUnknownFailureClassQaTests
{
    private const string TransportCanary = "r2-synthetic-transport-canary";
    private const string BucketCanary = "synthetic-bucket-canary";
    private const string ObjectCanary = "private/9f2c41/medical.pdf";

    [Theory]
    // A bare exception with no HTTP response and no recognized transport type.
    [InlineData("io")]
    [InlineData("malformed-response")]
    [InlineData("nested-unrecognized")]
    [InlineData("aggregate-of-unrecognized")]
    public void Unrecognized_no_response_failure_falls_back_to_unknown_without_guessing(string shape)
    {
        var failure = CreateUnrecognizedFailure(shape, TransportCanary);

        // The fallback must not invent a transport, TLS or DNS root cause that
        // the SDK never reported, and must not be reported as an S3 response.
        Assert.Equal("unknown",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(failure));
    }

    [Theory]
    [InlineData("put")]
    [InlineData("get")]
    [InlineData("delete")]
    [InlineData("head")]
    public async Task Unrecognized_no_response_failure_is_logged_as_unknown_and_fails_closed(string operation)
    {
        var logger = new CapturingLogger();
        using var storage = new S3CompatibleFileStorageService(
            new ThrowingObjectClient(CreateUnrecognizedFailure("io", TransportCanary)),
            Options.Create(new Taslim.Api.Files.FileOptions { S3Bucket = BucketCanary }),
            logger);

        using var content = new MemoryStream([1, 2, 3]);

        // Unknown is never treated as "the object is simply missing": a read or
        // existence probe must not return null/false, and no operation may be
        // reported as successful.
        await Assert.ThrowsAsync<FileStorageOperationException>(() => operation switch
        {
            "put" => storage.StoreAsync(ObjectCanary, content),
            "get" => storage.OpenReadAsync(ObjectCanary),
            "delete" => storage.DeleteAsync(ObjectCanary),
            "head" => storage.ExistsAsync(ObjectCanary),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        var message = Assert.Single(logger.Messages);

        // Operational diagnostics keep the useful shape: which operation, which
        // concrete SDK-side exception type, and which sanitized class.
        Assert.Contains($"Operation={operation}", message, StringComparison.Ordinal);
        Assert.Contains("ErrorType=IOException", message, StringComparison.Ordinal);
        Assert.Contains("FailureClass=unknown", message, StringComparison.Ordinal);

        // This is the non-AmazonS3Exception log branch: with no HTTP response the
        // adapter must not fabricate a status, and must not forward the
        // untrusted exception text, the bucket or the private object path.
        Assert.DoesNotContain("StatusCode=", message, StringComparison.Ordinal);
        Assert.DoesNotContain(TransportCanary, message, StringComparison.Ordinal);
        Assert.DoesNotContain(BucketCanary, message, StringComparison.Ordinal);
        Assert.DoesNotContain(ObjectCanary, message, StringComparison.Ordinal);
        Assert.DoesNotContain("medical.pdf", message, StringComparison.Ordinal);
        Assert.Empty(logger.Exceptions);
    }

    private static Exception CreateUnrecognizedFailure(string shape, string canary) => shape switch
    {
        "io" => new IOException(
            $"The response ended prematurely while contacting the endpoint. {canary}"),
        "malformed-response" => new InvalidDataException(
            $"Malformed provider descriptor near {canary}"),
        "nested-unrecognized" => new InvalidOperationException(
            "Object client failed",
            new ArgumentException(canary)),
        "aggregate-of-unrecognized" => new AggregateException(new InvalidOperationException(canary)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private sealed class CapturingLogger : ILogger<S3CompatibleFileStorageService>
    {
        public List<string> Messages { get; } = [];
        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
                Exceptions.Add(exception);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class ThrowingObjectClient(Exception failure) : IS3CompatibleObjectClient
    {
        public Task PutAsync(string bucket, string key, Stream content, CancellationToken cancellationToken = default)
            => Task.FromException(failure);

        public Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromException<Stream?>(failure);

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromException(failure);

        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(failure);

        public void Dispose() { }
    }
}
