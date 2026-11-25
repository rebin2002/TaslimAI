using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Net.Sockets;
using Amazon.S3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class S3CompatibleStorageDiagnosticsTests
{
    [Fact]
    public void Classifies_socket_failure_without_exposing_network_details()
    {
        var exception = new AmazonS3Exception("synthetic put failure", new SocketException((int)SocketError.HostNotFound));

        Assert.Equal("network", Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(exception));
    }

    [Fact]
    public void Classifies_tls_failure_without_exposing_certificate_or_endpoint_details()
    {
        var exception = new AmazonS3Exception("synthetic put failure", new AuthenticationException("synthetic TLS failure"));

        Assert.Equal("tls", Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(exception));
    }

    [Fact]
    public void Missing_http_response_is_distinguished_without_guessing_the_root_cause()
    {
        var noResponse = new AmazonS3Exception("synthetic opaque SDK failure");
        Assert.Equal("s3_no_http_response",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(noResponse));

        // An unrecognized nested exception must not hide the useful outer
        // AWS status category or leak its message into diagnostic output.
        var unknownInner = new AmazonS3Exception("synthetic opaque SDK failure",
            new InvalidOperationException("private connection details"));
        Assert.Equal("s3_no_http_response",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(unknownInner));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "s3_auth_or_permission")]
    [InlineData(HttpStatusCode.Forbidden, "s3_auth_or_permission")]
    [InlineData(HttpStatusCode.TooManyRequests, "s3_rate_limited")]
    [InlineData(HttpStatusCode.BadGateway, "s3_service_error")]
    [InlineData(HttpStatusCode.BadRequest, "s3_response")]
    public void Response_status_uses_a_sanitized_failure_class(
        HttpStatusCode statusCode, string expected)
    {
        var failure = new AmazonS3Exception("synthetic provider response")
        {
            StatusCode = statusCode,
        };
        Assert.Equal(expected,
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(failure));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NoSuchKey", true)]
    [InlineData(HttpStatusCode.NotFound, "NotFound", true)]
    [InlineData(HttpStatusCode.NotFound, null, true)]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket", false)]
    [InlineData(HttpStatusCode.Forbidden, "NoSuchKey", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "NotFound", false)]
    [InlineData(HttpStatusCode.BadRequest, "NoSuchKey", false)]
    [InlineData((HttpStatusCode)0, "NoSuchKey", false)]
    public void Only_object_not_found_responses_may_hide_missing_private_file(
        HttpStatusCode status, string? errorCode, bool expected)
    {
        // A misleading code on an auth, network, or bucket error must never
        // silently become a null GET response or false HEAD result.
        var exception = new AmazonS3Exception("synthetic failure")
        {
            StatusCode = status,
            ErrorCode = errorCode,
        };

        Assert.Equal(expected, AwsS3CompatibleObjectClient.IsNotFound(exception));
    }

    [Fact]
    public void Classifies_timeout_and_provider_response_failures_separately()
    {
        Assert.Equal(
            "timeout",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(new TaskCanceledException()));
        Assert.Equal(
            "transport",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(new HttpRequestException()));
        Assert.Equal(
            "s3_auth_or_permission",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(new AmazonS3Exception("synthetic response failure")
            {
                StatusCode = HttpStatusCode.Forbidden,
            }));
    }
    [Theory]
    [InlineData("put")]
    [InlineData("get")]
    [InlineData("delete")]
    [InlineData("head")]
    public async Task Sdk_timeout_without_caller_cancellation_is_a_storage_failure(string operation)
    {
        // A provider timeout must not be mistaken for a client-aborted request.
        using var storage = CreateThrowingStorage(new TaskCanceledException("synthetic upstream timeout"));
        using var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<FileStorageOperationException>(() => operation switch
        {
            "put" => storage.StoreAsync("private/key", content),
            "get" => storage.OpenReadAsync("private/key"),
            "delete" => storage.DeleteAsync("private/key"),
            "head" => storage.ExistsAsync("private/key"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });
    }

    [Theory]
    [InlineData("put")]
    [InlineData("get")]
    [InlineData("delete")]
    [InlineData("head")]
    public async Task Explicit_caller_cancellation_is_preserved(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var storage = CreateThrowingStorage(new OperationCanceledException(cancellation.Token));
        using var content = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "put" => storage.StoreAsync("private/key", content, cancellation.Token),
            "get" => storage.OpenReadAsync("private/key", cancellation.Token),
            "delete" => storage.DeleteAsync("private/key", cancellation.Token),
            "head" => storage.ExistsAsync("private/key", cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });
    }


    [Fact]
    public async Task Failed_put_logs_only_sanitized_failure_class_not_exception_or_object_identity()
    {
        // Reproduce the production symptom (S3 status zero) with synthetic
        // sensitive text. Diagnostic logging must never expose an SDK message,
        // bucket name, storage key, or a raw exception object.
        const string secretMarker = "synthetic-sensitive-credential-DO-NOT-LOG";
        var logger = new CapturingLogger();
        using var storage = new S3CompatibleFileStorageService(
            new ThrowingObjectClient(new AmazonS3Exception(
                $"Private endpoint and credential: {secretMarker}")),
            Options.Create(new Taslim.Api.Files.FileOptions { S3Bucket = $"bucket-{secretMarker}" }),
            logger);
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<FileStorageOperationException>(
            () => storage.StoreAsync($"private/{secretMarker}/medical.pdf", content));

        var message = Assert.Single(logger.Messages);
        Assert.Contains("Operation=put", message, StringComparison.Ordinal);
        Assert.Contains("StatusCode=0", message, StringComparison.Ordinal);
        Assert.Contains("FailureClass=s3_no_http_response", message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMarker, message, StringComparison.Ordinal);
        Assert.DoesNotContain("medical.pdf", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Private endpoint", message, StringComparison.Ordinal);
        Assert.Empty(logger.Exceptions);
    }

    [Fact]
    public async Task Failed_put_does_not_log_untrusted_provider_error_code_or_exception_message()
    {
        // S3 error codes are upstream-controlled strings. Preserve the HTTP
        // status and sanitized class without echoing arbitrary provider data.
        const string privateMarker = "synthetic-private-provider-detail-DO-NOT-LOG";
        var logger = new CapturingLogger();
        var upstreamFailure = new AmazonS3Exception($"Upstream endpoint {privateMarker}")
        {
            StatusCode = HttpStatusCode.Forbidden,
            ErrorCode = $"AccessDenied/{privateMarker}",
        };
        using var storage = new S3CompatibleFileStorageService(
            new ThrowingObjectClient(upstreamFailure),
            Options.Create(new Taslim.Api.Files.FileOptions { S3Bucket = "private-test-bucket" }),
            logger);
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<FileStorageOperationException>(
            () => storage.StoreAsync("private/test-object", content));

        var message = Assert.Single(logger.Messages);
        Assert.Contains("Operation=put", message, StringComparison.Ordinal);
        Assert.Contains("StatusCode=403", message, StringComparison.Ordinal);
        Assert.Contains("FailureClass=s3_auth_or_permission", message, StringComparison.Ordinal);
        Assert.DoesNotContain(privateMarker, message, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorCode=", message, StringComparison.Ordinal);
        Assert.Empty(logger.Exceptions);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("head")]
    [InlineData("delete")]
    public async Task Failed_private_object_operations_do_not_hide_permission_errors_or_log_identifiers(string operation)
    {
        // An upstream error code resembling a missing key must not turn a
        // permission failure into a missing file, nor expose private identity.
        const string privateMarker = "synthetic-private-storage-identity-DO-NOT-LOG";
        var logger = new CapturingLogger();
        var upstream = new AmazonS3Exception($"Private endpoint {privateMarker}")
        {
            StatusCode = HttpStatusCode.Forbidden,
            ErrorCode = $"NoSuchKey-{privateMarker}",
        };
        using var storage = new S3CompatibleFileStorageService(
            new ThrowingObjectClient(upstream),
            Options.Create(new Taslim.Api.Files.FileOptions { S3Bucket = $"bucket-{privateMarker}" }),
            logger);

        await Assert.ThrowsAsync<FileStorageOperationException>(() => operation switch
        {
            "get" => storage.OpenReadAsync($"private/{privateMarker}/medical.pdf"),
            "head" => storage.ExistsAsync($"private/{privateMarker}/medical.pdf"),
            "delete" => storage.DeleteAsync($"private/{privateMarker}/medical.pdf"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });

        var message = Assert.Single(logger.Messages);
        Assert.Contains($"Operation={operation}", message, StringComparison.Ordinal);
        Assert.Contains("StatusCode=403", message, StringComparison.Ordinal);
        Assert.Contains("FailureClass=s3_auth_or_permission", message, StringComparison.Ordinal);
        Assert.DoesNotContain(privateMarker, message, StringComparison.Ordinal);
        Assert.DoesNotContain("medical.pdf", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorCode=", message, StringComparison.Ordinal);
        Assert.Empty(logger.Exceptions);
    }

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

    private static S3CompatibleFileStorageService CreateThrowingStorage(Exception failure) => new(
        new ThrowingObjectClient(failure),
        Options.Create(new Taslim.Api.Files.FileOptions { S3Bucket = "test-bucket" }),
        NullLogger<S3CompatibleFileStorageService>.Instance);

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
