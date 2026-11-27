using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free, deterministic path-behavior contract for the direct video HTTP
/// adapter. Only an in-memory handler is used, so no provider, credential, or paid
/// call is reachable from these tests. The regression under test is that a base URL
/// carrying a non-root path prefix must survive URL composition for both the submit
/// path and the status path template, and that a misconfigured absolute path fails
/// closed before any request leaves the process.
/// </summary>
public sealed class DirectVideoHttpProviderAdapterTests
{
    [Fact]
    public async Task Submit_preserves_a_configured_base_path_without_a_trailing_slash()
    {
        Uri? requestUri = null;
        using var client = new HttpClient(new RecordingHandler(uri => requestUri = uri));
        var options = Options.Create(new DirectVideoProviderOptions
        {
            Enabled = true,
            ProviderKey = "synthetic-provider",
            ModelKey = "synthetic-model",
            ApiBaseUrl = "https://provider.invalid/api",
            ApiKey = "synthetic-key",
        });
        var adapter = new DirectVideoHttpProviderAdapter(
            client,
            options,
            new AllowAllProviderUrlPolicy(),
            NullLogger<DirectVideoHttpProviderAdapter>.Instance);

        var result = await adapter.SubmitAsync(new DirectVideoRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            MovieStudioOperations.SceneClip,
            "A provider-neutral synthetic shot.",
            5,
            "16:9",
            DirectVideoResolutions.Hd720,
            false,
            null,
            null,
            null));

        Assert.Equal("provider-job-1", result.ProviderJobId);
        Assert.Equal(new Uri("https://provider.invalid/api/video/generations"), requestUri);
    }

    [Theory]
    [InlineData("https://provider.invalid/api")]
    [InlineData("https://provider.invalid/api/")]
    [InlineData("https://provider.invalid")]
    [InlineData("https://provider.invalid/api/v1/")]
    public async Task Submit_preserves_the_configured_base_path_for_every_slash_shape(string apiBaseUrl)
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client, apiBaseUrl);

        var result = await adapter.SubmitAsync(Request());

        Assert.Equal("provider-job-1", result.ProviderJobId);
        Assert.Equal(new Uri(apiBaseUrl.TrimEnd('/') + "/video/generations"), handler.RequestUris.Single());
    }

    [Fact]
    public async Task Status_polling_resolves_the_configured_template_against_the_base_path()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client, "https://provider.invalid/api");

        await adapter.SubmitAsync(Request());
        var status = await adapter.GetStatusAsync("provider-job-1");

        Assert.Equal(DirectVideoJobStatus.Running, status.Status);
        Assert.Equal(42, status.ProgressPercent);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(new Uri("https://provider.invalid/api/video/generations"), handler.RequestUris[0]);
        Assert.Equal(new Uri("https://provider.invalid/api/video/generations/provider-job-1"), handler.RequestUris[1]);
    }

    [Fact]
    public async Task An_absolute_configured_submit_path_is_rejected_before_any_provider_request()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client, "https://provider.invalid/api", submitPath: "https://attacker.invalid/video/generations");

        var exception = await Assert.ThrowsAsync<DirectVideoProviderException>(() => adapter.SubmitAsync(Request()));

        Assert.Equal(DirectVideoErrorCategory.InvalidRequest, exception.Category);
        Assert.Equal(DirectVideoErrorCodes.RequestInvalid, exception.Code);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task An_unconfigured_base_url_is_rejected_before_any_provider_request()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var adapter = CreateAdapter(client, apiBaseUrl: string.Empty);

        var exception = await Assert.ThrowsAsync<DirectVideoProviderException>(() => adapter.SubmitAsync(Request()));

        Assert.Equal(DirectVideoErrorCategory.Unavailable, exception.Category);
        Assert.Equal(DirectVideoErrorCodes.ProviderUnavailable, exception.Code);
        Assert.Empty(handler.RequestUris);
    }

    private static DirectVideoHttpProviderAdapter CreateAdapter(
        HttpClient client,
        string apiBaseUrl,
        string submitPath = "video/generations") => new(
            client,
            Options.Create(new DirectVideoProviderOptions
            {
                Enabled = true,
                ProviderKey = "synthetic-provider",
                ModelKey = "synthetic-model",
                ApiBaseUrl = apiBaseUrl,
                ApiKey = "synthetic-key",
                SubmitPath = submitPath,
            }),
            new AllowAllProviderUrlPolicy(),
            NullLogger<DirectVideoHttpProviderAdapter>.Instance);

    private static DirectVideoRequest Request() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        MovieStudioOperations.SceneClip,
        "A provider-neutral synthetic shot.",
        5,
        "16:9",
        DirectVideoResolutions.Hd720,
        false,
        null,
        null,
        null);

    private sealed class RecordingHandler(Action<Uri?>? record = null) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            record?.Invoke(request.RequestUri);
            object body = request.Method == HttpMethod.Get
                ? new { id = "provider-job-1", status = "running", progressPercent = 42 }
                : new { id = "provider-job-1" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }

    private sealed class AllowAllProviderUrlPolicy : IProviderUrlPolicy
    {
        public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
