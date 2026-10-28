using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Net.Sockets;
using Amazon.S3;
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
}
