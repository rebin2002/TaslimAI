using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ManusMovieVideoProviderTests
{
    private const string TaskId = "manus_task_123";

    [Fact]
    public async Task Disabled_configuration_never_calls_Manus_and_is_unavailable()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("should not call"));
        var options = EnabledOptions();
        options.Enabled = false;
        var provider = CreateProvider(handler, options);

        Assert.False(provider.IsAvailable);
        await Assert.ThrowsAsync<MovieProviderUnavailableException>(() => provider.SubmitAsync(Request(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Submission_maps_private_task_request_prompt_and_reference_image_without_leaking_key()
    {
        var handler = new RecordingHandler(request => JsonResponse($"{{\"ok\":true,\"task_id\":\"{TaskId}\"}}"));
        var provider = CreateProvider(handler, EnabledOptions());

        var submission = await provider.SubmitAsync(Request(sourceImageUri: "https://assets.example.test/frame.png"), CancellationToken.None);

        Assert.Equal(TaskId, submission.ProviderJobId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v2/task.create", request.RequestUri!.AbsoluteUri);
        Assert.Equal("synthetic-manus-token", request.Headers.GetValues("x-manus-api-key").Single());
        var payload = JsonDocument.Parse(handler.RequestBodies.Single()).RootElement;
        Assert.False(payload.GetProperty("interactive_mode").GetBoolean());
        Assert.True(payload.GetProperty("hide_in_task_list").GetBoolean());
        Assert.Equal("private", payload.GetProperty("share_visibility").GetString());
        Assert.Equal("standard", payload.GetProperty("agent_profile").GetString());
        var content = payload.GetProperty("message").GetProperty("content");
        Assert.Contains("A camera moves slowly", content[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("https://assets.example.test/frame.png", content[1].GetProperty("file_url").GetString());
        Assert.DoesNotContain("synthetic-manus-token", handler.RequestBodies.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_normalizes_running_and_completed_attachment_with_internal_credit_estimate()
    {
        var calls = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("task.detail", StringComparison.Ordinal))
            {
                calls++;
                return JsonResponse(calls == 1
                    ? $"{{\"ok\":true,\"task\":{{\"id\":\"{TaskId}\",\"status\":\"running\",\"credit_usage\":12}}}}"
                    : $"{{\"ok\":true,\"task\":{{\"id\":\"{TaskId}\",\"status\":\"stopped\",\"credit_usage\":12}}}}");
            }

            return JsonResponse(calls == 1
                ? "{\"ok\":true,\"messages\":[]}"
                : $"{{\"ok\":true,\"task_id\":\"{TaskId}\",\"messages\":[{{\"type\":\"assistant_message\",\"assistant_message\":{{\"attachments\":[{{\"type\":\"file\",\"filename\":\"clip.mp4\",\"url\":\"https://cdn.example.test/clip.mp4\",\"content_type\":\"video/mp4\",\"duration_seconds\":5}}]}}}}]}}");
        });
        var provider = CreateProvider(handler, EnabledOptions());

        var running = await provider.GetStatusAsync(TaskId, CancellationToken.None);
        var completed = await provider.GetStatusAsync(TaskId, CancellationToken.None);

        Assert.Equal(MovieVideoProviderJobStatus.Running, running.Status);
        Assert.Equal(MovieVideoProviderJobStatus.Succeeded, completed.Status);
        Assert.Equal(0.30m, completed.EstimatedCostUsd);
        Assert.Equal(100, completed.ProgressPercent);
        Assert.Contains("outputAvailable", completed.SafeMetadataJson, StringComparison.Ordinal);
        Assert.Contains("estimatedCostUsd", completed.SafeMetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retrieval_downloads_only_https_video_and_preserves_duration_and_cost()
    {
        var bytes = Encoding.UTF8.GetBytes("clip");
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
                return BinaryResponse(bytes, "video/mp4");
            if (request.Method == HttpMethod.Get && request.RequestUri!.Host == "cdn.example.test")
                return BinaryResponse(bytes, "video/mp4");
            if (request.RequestUri!.AbsolutePath.EndsWith("task.detail", StringComparison.Ordinal))
                return JsonResponse($"{{\"ok\":true,\"task\":{{\"id\":\"{TaskId}\",\"status\":\"stopped\",\"credit_usage\":12}}}}");
            return JsonResponse($"{{\"ok\":true,\"task_id\":\"{TaskId}\",\"messages\":[{{\"type\":\"assistant_message\",\"assistant_message\":{{\"attachments\":[{{\"type\":\"file\",\"filename\":\"clip.mp4\",\"url\":\"https://cdn.example.test/clip.mp4\",\"content_type\":\"video/mp4\",\"duration_seconds\":5}}]}}}}]}}");
        });
        var provider = CreateProvider(handler, EnabledOptions());
        var status = await provider.GetStatusAsync(TaskId, CancellationToken.None);

        var output = await provider.RetrieveAsync(TaskId, status, CancellationToken.None);
        await using var stream = await output.OpenReadAsync(CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal("video/mp4", output.ContentType);
        Assert.Equal("clip.mp4", output.FileName);
        Assert.Equal(4, output.SizeBytes);
        Assert.Equal(5, output.DurationSeconds);
        Assert.Equal("clip", Encoding.UTF8.GetString(copy.ToArray()));
        Assert.Equal(0.30m, output.EstimatedCostUsd);
        Assert.Equal("USD", output.Currency);
    }

    [Fact]
    public async Task Error_status_and_cancellation_are_safe_and_use_task_stop()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("task.stop", StringComparison.Ordinal))
                return JsonResponse("{\"ok\":true}");
            if (request.RequestUri.AbsolutePath.EndsWith("task.detail", StringComparison.Ordinal))
                return JsonResponse($"{{\"ok\":true,\"task\":{{\"id\":\"{TaskId}\",\"status\":\"error\"}}}}");
            return JsonResponse("{\"ok\":true,\"messages\":[]}");
        });
        var provider = CreateProvider(handler, EnabledOptions());

        var failed = await provider.GetStatusAsync(TaskId, CancellationToken.None);
        await provider.CancelAsync(TaskId, CancellationToken.None);

        Assert.Equal(MovieVideoProviderJobStatus.Failed, failed.Status);
        Assert.DoesNotContain("private provider details", failed.SafeMetadataJson, StringComparison.Ordinal);
        Assert.Contains(handler.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("task.stop", StringComparison.Ordinal));
        var stopBody = handler.RequestBodies.Last(body => body.Contains("task_id", StringComparison.Ordinal));
        Assert.Equal(TaskId, JsonDocument.Parse(stopBody).RootElement.GetProperty("task_id").GetString());
    }

    [Fact]
    public async Task Unsupported_request_and_non_video_output_are_rejected()
    {
        var provider = CreateProvider(new RecordingHandler(_ => JsonResponse($"{{\"ok\":true,\"task_id\":\"{TaskId}\"}}")), EnabledOptions());

        await Assert.ThrowsAsync<MovieVideoProviderException>(() => provider.SubmitAsync(Request(aspectRatio: "4:5"), CancellationToken.None));

        var metadataStatus = new MovieVideoProviderStatus(
            MovieVideoProviderJobStatus.Succeeded,
            100,
            MetadataJson: JsonSerializer.Serialize(new
            {
                providerTaskId = TaskId,
                outputUrl = "https://cdn.example.test/clip.mp4",
                contentType = "video/mp4",
                fileName = "clip.mp4",
                sizeBytes = 4,
                durationSeconds = 5,
            }));
        var badHandler = new RecordingHandler(request => BinaryResponse(Encoding.UTF8.GetBytes("clip"), "text/plain"));
        var badProvider = CreateProvider(badHandler, EnabledOptions());
        var output = await badProvider.RetrieveAsync(TaskId, metadataStatus, CancellationToken.None);

        await Assert.ThrowsAsync<MovieVideoProviderOutputException>(() => output.OpenReadAsync(CancellationToken.None));
    }

    private static ManusMovieVideoProvider CreateProvider(HttpMessageHandler handler, MovieVideoOptions options) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.manus.ai/") }, Options.Create(options), NullLogger<ManusMovieVideoProvider>.Instance);

    private static MovieVideoOptions EnabledOptions() => new()
    {
        Enabled = true,
        ProviderKey = "manus",
        ProviderTimeoutSeconds = 15,
        MaxOutputBytes = 1024,
        MaxTransientRetries = 0,
        Manus = new ManusMovieVideoOptions
        {
            Enabled = true,
            ApiKey = "synthetic-manus-token",
            ApiBaseUrl = "https://api.manus.ai/",
            AgentProfile = "standard",
            CreditUsdPerCredit = 0.025m,
            MaxPromptCharacters = 12_000,
            MaxTaskMessages = 50,
        },
    };

    private static MovieVideoGenerationRequest Request(
        string aspectRatio = "16:9",
        string? sourceImageUri = null) => new(
            MovieStudioOperations.SceneClip,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "A camera moves slowly through a sunlit forest.",
            5,
            aspectRatio,
            "cinematic",
            "en",
            "No text overlays.",
            "Visual language: natural light",
            "Scene: forest",
            "Shot: slow dolly",
            sourceImageUri);

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage BinaryResponse(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = bytes.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return responseFactory(request);
        }
    }
}
