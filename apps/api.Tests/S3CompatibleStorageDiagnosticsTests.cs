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
    public void Classifies_timeout_and_provider_response_failures_separately()
    {
        Assert.Equal(
            "timeout",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(new TaskCanceledException()));
        Assert.Equal(
            "transport",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(new HttpRequestException()));
        Assert.Equal(
            "s3_response",
            Taslim.Api.Files.S3CompatibleFileStorageService.ClassifyFailure(new AmazonS3Exception("synthetic response failure")
            {
                StatusCode = HttpStatusCode.Forbidden,
            }));
    }
}
