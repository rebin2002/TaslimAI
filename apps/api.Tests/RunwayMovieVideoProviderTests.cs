using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class RunwayMovieVideoProviderTests
{
    private static readonly Guid TaskId = Guid.Parse("8d2d2e6e-88c7-4c47-a6ab-7c6ce6e1a6a4");

    [Fact]
    public async Task Disabled_configuration_never_calls_Runway_and_is_unavailable()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("should not call"));
        var provider = CreateProvider(handler, new MovieVideoOptions { Enabled = false, ProviderKey = "runway", ApiKey = "secret" });

        Assert.False(provider.IsAvailable);
        await Assert.ThrowsAsync<MovieProviderUnavailableException>(() => provider.SubmitAsync(Request(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Submission_maps_prompt_duration_ratio_and_reference_image_without_leaking_secrets()
    {
        var handler = new RecordingHandler(_ => JsonResponse($"{{\"id\":\"{TaskId}\",\"estimatedCost\":{{\"credits\":60}}}}"));
        var provider = CreateProvider(handler, EnabledOptions());

        var submission = await provider.SubmitAsync(Request(sourceImageUri: "https://assets.example.test/frame.png"), CancellationToken.None);

        Assert.Equal(TaskId.ToString(), submission.ProviderJobId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/image_to_video", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("synthetic-runway-token", request.Headers.Authorization.Parameter);
        Assert.Equal("2024-11-06", request.Headers.GetValues("X-Runway-Version").Single());
        var payload = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Equal("gen4.5", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("1280:720", payload.RootElement.GetProperty("ratio").GetString());
        Assert.Equal(5, payload.RootElement.GetProperty("duration").GetInt32());
        Assert.Equal("https://assets.example.test/frame.png", payload.RootElement.GetProperty("promptImage").GetString());
        Assert.DoesNotContain("synthetic-runway-token", payload.RootElement.GetProperty("promptText").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polling_normalizes_queued_generating_and_completed_states_and_preserves_cost_metadata()
    {
        var calls = 0;
        var handler = new RecordingHandler(request =>
        {
            calls++;
            return JsonResponse(calls switch
            {
                1 => $"{{\"id\":\"{TaskId}\",\"status\":\"PENDING\",\"estimatedCost\":{{\"credits\":60}}}}",
                2 => $"{{\"id\":\"{TaskId}\",\"status\":\"RUNNING\",\"estimatedCost\":{{\"credits\":60}}}}",
                _ => $"{{\"id\":\"{TaskId}\",\"status\":\"SUCCEEDED\",\"output\":[\"https://cdn.example.test/clip.mp4\"],\"estimatedCost\":{{\"credits\":60}},\"outputMetadata\":{{\"contentType\":\"video/mp4\",\"fileName\":\"clip.mp4\",\"sizeBytes\":4,\"durationSeconds\":5}}}}",
            });
        });
        var provider = CreateProvider(handler, EnabledOptions());

        var queued = await provider.GetStatusAsync(TaskId.ToString(), CancellationToken.None);
        var running = await provider.GetStatusAsync(TaskId.ToString(), CancellationToken.None);
        var completed = await provider.GetStatusAsync(TaskId.ToString(), CancellationToken.None);

        Assert.Equal(MovieVideoProviderJobStatus.Queued, queued.Status);
        Assert.Equal(MovieVideoProviderJobStatus.Running, running.Status);
        Assert.Equal(MovieVideoProviderJobStatus.Succeeded, completed.Status);
        Assert.Equal(0.60m, completed.EstimatedCostUsd);
        Assert.Contains("estimatedCostUsd", completed.SafeMetadataJson);
        Assert.Contains("continuation", completed.SafeMetadataJson);
    }

    [Fact]
    public async Task Completion_retrieves_https_video_and_validates_mime_size_and_duration()
    {
        var bytes = Encoding.UTF8.GetBytes("clip");
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get && request.RequestUri!.Host == "cdn.example.test"
            ? BinaryResponse(bytes, "video/mp4")
            : JsonResponse($"{{\"id\":\"{TaskId}\",\"status\":\"SUCCEEDED\",\"output\":[\"https://cdn.example.test/clip.mp4\"],\"estimatedCost\":{{\"credits\":60}},\"outputMetadata\":{{\"contentType\":\"video/mp4\",\"fileName\":\"clip.mp4\",\"sizeBytes\":4,\"durationSeconds\":5}}}}"));
        var provider = CreateProvider(handler, EnabledOptions());
        var status = await provider.GetStatusAsync(TaskId.ToString(), CancellationToken.None);

        var output = await provider.RetrieveAsync(TaskId.ToString(), status, CancellationToken.None);
        await using var stream = await output.OpenReadAsync(CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal("video/mp4", output.ContentType);
        Assert.Equal("clip.mp4", output.FileName);
        Assert.Equal(4, output.SizeBytes);
        Assert.Equal(5, output.DurationSeconds);
        Assert.Equal("clip", Encoding.UTF8.GetString(copy.ToArray()));
        Assert.Equal(0.60m, output.EstimatedCostUsd);
        Assert.Equal(0.60m, output.EstimatedCostUsd);
        Assert.Null(output.ActualCostUsd);
        Assert.Contains("USD", output.Currency);
    }

    [Fact]
    public async Task Failed_and_cancelled_tasks_are_normalized_without_provider_error_body()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : JsonResponse($"{{\"id\":\"{TaskId}\",\"status\":\"FAILED\",\"failureCode\":\"vendor-secret-body\",\"failureReason\":\"private provider details\"}}"));
        var provider = CreateProvider(handler, EnabledOptions());

        var failed = await provider.GetStatusAsync(TaskId.ToString(), CancellationToken.None);
        Assert.Equal(MovieVideoProviderJobStatus.Failed, failed.Status);
        Assert.DoesNotContain("vendor-secret-body", failed.SafeMetadataJson, StringComparison.Ordinal);
        await provider.CancelAsync(TaskId.ToString(), CancellationToken.None);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Provider_http_timeout_is_normalized_without_waiting_indefinitely()
    {
        var options = EnabledOptions();
        options.ProviderTimeoutSeconds = 1;
        var provider = CreateProvider(new SlowHandler(), options);

        await Assert.ThrowsAsync<MovieVideoProviderTimeoutException>(() => provider.GetStatusAsync(TaskId.ToString(), CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_output_is_rejected_and_private_provider_errors_are_sanitized()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get && request.RequestUri!.Host == "cdn.example.test"
            ? BinaryResponse(Encoding.UTF8.GetBytes("clip"), "text/plain")
            : JsonResponse($"{{\"id\":\"{TaskId}\",\"status\":\"SUCCEEDED\",\"output\":[\"https://cdn.example.test/clip.mp4\"],\"outputMetadata\":{{\"contentType\":\"video/mp4\",\"fileName\":\"clip.mp4\",\"sizeBytes\":4}}}}"));
        var provider = CreateProvider(handler, EnabledOptions());
        var status = await provider.GetStatusAsync(TaskId.ToString(), CancellationToken.None);
        var output = await provider.RetrieveAsync(TaskId.ToString(), status, CancellationToken.None);

        await Assert.ThrowsAsync<MovieVideoProviderOutputException>(() => output.OpenReadAsync(CancellationToken.None));

        handler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("synthetic-runway-token provider error body"),
        };
        var exception = await Assert.ThrowsAsync<MovieVideoProviderException>(() => provider.GetStatusAsync(TaskId.ToString(), CancellationToken.None));
        Assert.DoesNotContain("synthetic-runway-token", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider error body", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_duration_ratio_and_continuation_are_explicitly_rejected()
    {
        var provider = CreateProvider(new RecordingHandler(_ => JsonResponse($"{{\"id\":\"{TaskId}\"}}")), EnabledOptions());

        await Assert.ThrowsAsync<MovieVideoProviderException>(() => provider.SubmitAsync(Request(durationSeconds: 30), CancellationToken.None));
        await Assert.ThrowsAsync<MovieVideoProviderException>(() => provider.SubmitAsync(Request(aspectRatio: "4:5"), CancellationToken.None));
        await Assert.ThrowsAsync<MovieVideoProviderException>(() => provider.SubmitAsync(Request(continuationProviderJobId: TaskId.ToString()), CancellationToken.None));
    }

    [Fact]
    public async Task Execution_store_rejects_stale_claimant_writes_and_allows_current_claimant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Movie Test", Slug = $"movie-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"movie-{Guid.NewGuid():N}@example.test", NormalizedUserName = "MOVIE", Email = "movie@example.test", NormalizedEmail = "MOVIE@EXAMPLE.TEST", DisplayName = "Movie", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var job = new GenerationJob { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreatedByUserId = user.Id, JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Running, InputJson = "{}", ConcurrencyToken = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
        var movie = new MovieProject { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreatedByUserId = user.Id, Title = "Movie", Description = "Description", DurationSeconds = 5, Mode = MovieProjectModes.Full, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = DateTime.UtcNow } };
        var clip = new MovieClip { Id = Guid.NewGuid(), MovieProjectId = movie.Id, GenerationJobId = job.Id, Status = MovieClipStatuses.Queued, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.AddRange(workspace, user, new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, JoinedAt = DateTime.UtcNow }, job, movie, clip);
        await db.SaveChangesAsync();
        var execution = new MovieVideoProviderExecution { Id = Guid.NewGuid(), GenerationJobId = job.Id, MovieClipId = clip.Id, ProviderKey = "runway", Status = MovieVideoExecutionStatuses.Submitted, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MovieVideoProviderExecutions.Add(execution);
        await db.SaveChangesAsync();
        var store = new MovieVideoExecutionStore(db, Options.Create(new MovieVideoOptions { ClaimLeaseMinutes = 15 }));
        var staleToken = Guid.NewGuid();

        await Assert.ThrowsAsync<MovieVideoStaleWorkerException>(() => store.PersistSubmittedAsync(execution, staleToken, TaskId.ToString(), CancellationToken.None));
        await Assert.ThrowsAsync<MovieVideoStaleWorkerException>(() => store.TouchAsync(job.Id, staleToken, CancellationToken.None));
        Assert.Null((await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync()).ProviderJobId);

        await store.PersistSubmittedAsync(execution, job.ConcurrencyToken, TaskId.ToString(), CancellationToken.None);
        Assert.Equal(TaskId.ToString(), (await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync()).ProviderJobId);

        await db.GenerationJobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, GenerationJobStatus.Succeeded));
        await store.MarkFailedAsync(job.Id, job.ConcurrencyToken, GenerationJobErrorCodes.MovieGenerationFailed, CancellationToken.None);
        var afterLateFailure = await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync();
        var afterLateClip = await db.MovieClips.AsNoTracking().SingleAsync();
        Assert.Equal(MovieVideoExecutionStatuses.Queued, afterLateFailure.Status);
        Assert.Equal(MovieClipStatuses.Generating, afterLateClip.Status);

        await store.MarkReadyAsync(job.Id, job.ConcurrencyToken, null, null, 5, "{\"durationSeconds\":5}", CancellationToken.None);
        Assert.Equal(MovieVideoExecutionStatuses.Completed, (await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(MovieClipStatuses.Ready, (await db.MovieClips.AsNoTracking().SingleAsync()).Status);
    }

    private static RunwayMovieVideoProvider CreateProvider(HttpMessageHandler handler, MovieVideoOptions options) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.dev.runwayml.com/v1/") }, Options.Create(options), NullLogger<RunwayMovieVideoProvider>.Instance);

    private static MovieVideoOptions EnabledOptions() => new()
    {
        Enabled = true,
        ProviderKey = "runway",
        ApiKey = "synthetic-runway-token",
        ApiBaseUrl = "https://api.dev.runwayml.com/v1/",
        Model = "gen4.5",
        CreditUsd = 0.01m,
        ProviderTimeoutSeconds = 15,
        MaxOutputBytes = 1024,
        MaxTransientRetries = 0,
    };

    private static MovieVideoGenerationRequest Request(
        int durationSeconds = 5,
        string aspectRatio = "16:9",
        string? sourceImageUri = null,
        string? continuationProviderJobId = null) => new(
            MovieStudioOperations.SceneClip,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "A camera moves slowly through a sunlit forest.",
            durationSeconds,
            aspectRatio,
            "cinematic",
            "en",
            "No text overlays.",
            "Visual language: natural light",
            "Scene: forest",
            "Shot: slow dolly",
            sourceImageUri,
            continuationProviderJobId);

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
        public Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } = responseFactory;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null) RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return ResponseFactory(request);
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return JsonResponse("{}");
        }
    }
}
