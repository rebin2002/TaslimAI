using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieOperationalNoWorkerFactory : MovieOperationalApiFactory
{
    protected override bool WorkerEnabled => false;
}

public sealed class MovieStudioIdempotencyTests : IClassFixture<MovieOperationalNoWorkerFactory>
{
    private readonly MovieOperationalNoWorkerFactory factory;

    public MovieStudioIdempotencyTests(MovieOperationalNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Reused_movie_job_reconciles_to_canonical_clip_without_persisting_a_duplicate()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie idempotency reconciliation");
        const string idempotencyKey = "movie-clip-reconcile-1";

        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Canonical clip" },
            idempotencyKey: idempotencyKey);

        // Reproduce the small race window after GenerationJobService has committed
        // the canonical job but before the winning request links its clip row.
        // The next request must repair that link and discard its speculative clip.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            await db.MovieClips
                .Where(item => item.Id == first.ClipId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GenerationJobId, (Guid?)null));
        }

        var replay = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Canonical clip" },
            idempotencyKey: idempotencyKey);

        Assert.Equal(first.Job.Id, replay.Job.Id);
        Assert.Equal(first.ClipId, replay.ClipId);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await verifyDb.GenerationJobs.CountAsync(item => item.IdempotencyKey == idempotencyKey));
        var clips = await verifyDb.MovieClips
            .AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id)
            .ToListAsync();
        var clip = Assert.Single(clips);
        Assert.Equal(first.ClipId, clip.Id);
        Assert.Equal(first.Job.Id, clip.GenerationJobId);
    }

    [Fact]
    public async Task Reusing_a_clip_key_with_a_different_title_is_rejected_without_an_extra_clip()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie idempotency mismatch");
        const string key = "movie-clip-title-mismatch-1";
        var path = $"/api/movie-studio/shots/{fixture.Shot.Id}/generate";

        var first = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, path, new { title = "Original clip" }, idempotencyKey: key);

        // Exercise the already-linked fast path. The old behavior silently
        // returned the original clip despite a materially different request.
        using var conflicting = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post, path, new { title = "Changed clip" }, idempotencyKey: key);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, conflicting.StatusCode);
        Assert.Contains(
            "IDEMPOTENCY_KEY_REUSED",
            await conflicting.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.CountAsync(job => job.IdempotencyKey == key));
        var clip = Assert.Single(await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == fixture.Project.Project.Id).ToListAsync());
        Assert.Equal(first.ClipId, clip.Id);
        Assert.Equal(first.Job.Id, clip.GenerationJobId);
    }

    [Fact]
    public async Task Rejected_overlong_key_does_not_leave_a_speculative_movie_clip()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie rejected key cleanup");
        var key = new string('x', 81);

        // GenerationJobService rejects keys longer than 80 chars *after*
        // QueueClipAsync persists its speculative clip. Rejection must not
        // strand a queued clip without an associated job.
        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(
            client, HttpMethod.Post,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Never queued" }, idempotencyKey: key);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("IDEMPOTENCY_KEY_INVALID",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(0, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == key));
    }

    [Fact]
    public void Invalid_or_non_movie_job_input_is_not_treated_as_a_clip_target()
    {
        Assert.False(MovieStudioIdempotency.TryGetMovieClipId(null, out _));
        Assert.False(MovieStudioIdempotency.TryGetMovieClipId("{not-json}", out _));
        Assert.False(MovieStudioIdempotency.TryGetMovieClipId("{}", out _));
    }

    [Fact]
    public async Task Completing_the_first_job_before_clip_link_publishes_the_canonical_private_asset()
    {
        using var client = factory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Movie early completion");
        var queued = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client, $"/api/movie-studio/shots/{fixture.Shot.Id}/generate",
            new { title = "Early completed clip" },
            idempotencyKey: "movie-first-completion-before-link");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.SingleAsync(item => item.Id == queued.Job.Id);
        var now = DateTime.UtcNow;
        job.Status = GenerationJobStatus.Succeeded;
        job.ConcurrencyToken = Guid.NewGuid();
        await db.MovieClips.Where(item => item.Id == queued.ClipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GenerationJobId, (Guid?)null));

        // Deterministically model worker completion inside the first-request
        // job-save/clip-link gap, with no paid provider or file-store call.
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = job.WorkspaceId,
            ProjectId = job.ProjectId,
            UserId = job.CreatedByUserId,
            OriginalFileName = "private-movie.mp4",
            StoredFileName = "private-movie.mp4",
            ContentType = "video/mp4",
            Extension = ".mp4",
            StorageKey = "tests/movie-first-completion.mp4",
            CreatedAt = now,
        };
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            WorkspaceId = job.WorkspaceId,
            ProjectId = job.ProjectId,
            CreatedByUserId = job.CreatedByUserId,
            SourceGenerationJobId = job.Id,
            StoredFileId = file.Id,
            Name = "Private completed movie clip",
            AssetType = AssetTypes.Video,
            MimeType = "video/mp4",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.StoredFiles.Add(file);
        db.Assets.Add(asset);
        db.MovieVideoProviderExecutions.Add(new MovieVideoProviderExecution
        {
            Id = Guid.NewGuid(),
            GenerationJobId = job.Id,
            MovieClipId = queued.ClipId,
            ProviderKey = "fake-movie",
            Status = MovieVideoExecutionStatuses.Running,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var executions = scope.ServiceProvider.GetRequiredService<MovieVideoExecutionStore>();
        await executions.MarkReadyAsync(job.Id, job.ConcurrencyToken, asset.Id, file.Id, 5, null, CancellationToken.None);

        var completedClip = await db.MovieClips.AsNoTracking().SingleAsync(item => item.Id == queued.ClipId);
        Assert.Equal(job.Id, completedClip.GenerationJobId);
        Assert.Equal(MovieClipStatuses.Ready, completedClip.Status);
        Assert.Equal(asset.Id, completedClip.AssetId);
        Assert.Equal(file.Id, completedClip.StoredFileId);
        Assert.Equal(1, await db.MovieClips.CountAsync(item => item.MovieProjectId == fixture.Project.Project.Id));
        Assert.Equal(job.WorkspaceId, await db.Assets.AsNoTracking()
            .Where(item => item.Id == asset.Id).Select(item => item.WorkspaceId).SingleAsync());
    }
}
