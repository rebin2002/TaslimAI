using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DirectVideoHttpProviderAdapterTests
{
    [Fact]
    public async Task Submit_preserves_a_configured_base_path_without_a_trailing_slash()
    {
        Uri? requestUri = null;
        using var client = new HttpClient(new RecordingHandler(request => requestUri = request.RequestUri));
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

    private sealed class RecordingHandler(Action<HttpRequestMessage> record) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            record(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "provider-job-1" }),
            });
        }
    }

    private sealed class AllowAllProviderUrlPolicy : IProviderUrlPolicy
    {
        public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
