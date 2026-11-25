using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free read-model and wire-serialization regression for the 120-minute
/// (7,200 second) Movie *project metadata* ceiling tracked in issue #685.
///
/// Contract under test, stated precisely: the planned total runtime of a long-form
/// project is ordinary project metadata that every read model must carry verbatim. It is
/// NOT a provider clip duration. Single-clip capability stays independently gated by the
/// clip-admission suite (#678/#679); nothing here submits, prices or queues a clip, and no
/// video provider, cost reservation, job, purchase or deployment is reachable from this file.
///
/// Why this coverage is new rather than a duplicate: the owner branch already proves that a
/// 7,200-second project can be created and that the detail projection plus the
/// duration-budget totals return 7,200. Nobody exercises the *lightweight shell* projection
/// (<c>GET /api/movie-studio/projects/{id}/shell</c>), which is the exact read model the web
/// client binds project metadata to, nor that the two independent projections agree with the
/// persisted value, nor that the camelCase wire field survives serialization. A later clamp
/// such as <c>Math.Min(movie.DurationSeconds, 3600)</c> added inside the shell projection, a
/// reordered positional record, or a renamed/missing JSON field would silently truncate a
/// two-hour project for users while the heavyweight detail projection still looked correct.
///
/// Determinism: the ceilings are asserted from persisted metadata that this test seeds
/// directly through the DbContext (the column is an unbounded Int32 and is representable
/// regardless of create-request policy), so the assertions hold on current <c>main</c> and
/// continue to hold once the long-form create ceiling is reconciled. No wall-clock, locale,
/// network or provider dependency.
/// </summary>
public sealed class MovieLongFormProjectMetadataReadModelTests : IClassFixture<MovieOperationalApiFactory>
{
    /// <summary>The approved long-form project metadata ceiling: 120 minutes.</summary>
    private const int TwoHourProjectSeconds = 7_200;

    /// <summary>The legacy short-form ceiling that must never be re-applied to project metadata.</summary>
    private const int LegacySixtyMinuteClampSeconds = 3_600;

    /// <summary>A value below every historical ceiling, used for the policy-independent comparison.</summary>
    private const int ShortProjectSeconds = 600;

    private readonly MovieOperationalApiFactory factory;

    public MovieLongFormProjectMetadataReadModelTests(MovieOperationalApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// The shell read model is reached only through <c>MovieStudioService.GetShellAsync</c>
    /// today. This pins its wire contract at the two-hour metadata ceiling and proves it does
    /// not silently fall back to the sixty-minute short-form clamp.
    /// </summary>
    [Fact]
    public async Task Shell_read_model_carries_the_full_two_hour_project_runtime_without_a_sixty_minute_clamp()
    {
        var (client, workspaceId, movieProjectId) = await CreateProjectAsync(
            "Long Form Shell Read Model", ShortProjectSeconds);

        SetPersistedDuration(movieProjectId, TwoHourProjectSeconds);

        using var response = await client.GetAsync($"/api/movie-studio/projects/{movieProjectId}/shell");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(
            TwoHourProjectSeconds,
            root.GetProperty("durationSeconds").GetInt32());

        // Guards the failure mode that matters for long-form: the read model must not
        // reintroduce the historical 3,600 second project ceiling.
        Assert.NotEqual(LegacySixtyMinuteClampSeconds, root.GetProperty("durationSeconds").GetInt32());

        // The web client binds the shell through this exact camelCase field name; a rename or
        // omission desynchronises project metadata on the client while the API still returns 200.
        Assert.True(
            root.TryGetProperty("durationSeconds", out _),
            $"shell projection omitted the durationSeconds wire field: {body}");

        // Metadata is a pure read: reaching a two-hour ceiling must never imply render work.
        Assert.Equal(MovieProjectModes.Full, root.GetProperty("mode").GetString());
        AssertNoGenerationSideEffects(workspaceId, movieProjectId);
    }

    /// <summary>
    /// The detail projection and the shell projection are built by different code paths. They
    /// must agree with each other and with storage, so a clamp introduced in only one of them
    /// cannot pass unnoticed. This assertion is independent of any create-request policy.
    /// </summary>
    [Theory]
    [InlineData(ShortProjectSeconds)]
    [InlineData(TwoHourProjectSeconds)]
    public async Task Detail_and_shell_projections_agree_with_persisted_metadata(int durationSeconds)
    {
        var (client, workspaceId, movieProjectId) = await CreateProjectAsync(
            "Long Form Projection Parity", ShortProjectSeconds);

        SetPersistedDuration(movieProjectId, durationSeconds);

        using var shellResponse = await client.GetAsync($"/api/movie-studio/projects/{movieProjectId}/shell");
        Assert.Equal(HttpStatusCode.OK, shellResponse.StatusCode);
        using var shellDocument = JsonDocument.Parse(await shellResponse.Content.ReadAsStringAsync());

        using var detailResponse = await client.GetAsync($"/api/movie-studio/projects/{movieProjectId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailDocument = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());

        var shellDuration = shellDocument.RootElement.GetProperty("durationSeconds").GetInt32();
        var detailDuration = detailDocument.RootElement.GetProperty("durationSeconds").GetInt32();

        var persisted = await ReadPersistedDurationAsync(movieProjectId);

        Assert.Equal(durationSeconds, persisted);
        Assert.Equal(persisted, shellDuration);
        Assert.Equal(persisted, detailDuration);

        // A long-form ceiling read must not be mistaken for a render request.
        AssertNoGenerationSideEffects(workspaceId, movieProjectId);
    }

    private async Task<(HttpClient Client, Guid WorkspaceId, Guid MovieProjectId)> CreateProjectAsync(
        string title, int durationSeconds)
    {
        var client = factory.CreateClient();
        var owner = await MovieOperationalFixtures.RegisterAsync(client, title);
        var created = await MovieOperationalFixtures.PostAsync<MovieStudioProjectResponse>(
            client,
            "/api/movie-studio/projects",
            new
            {
                workspaceId = owner.PersonalWorkspace.Id,
                mode = MovieProjectModes.Full,
                title,
                description = "Provider-free long-form project metadata read model fixture.",
                durationSeconds,
                aspectRatio = "16:9",
                style = "cinematic",
                language = "en",
            });

        Assert.Equal(durationSeconds, created.Project.DurationSeconds);
        return (client, owner.PersonalWorkspace.Id, created.Project.Id);
    }

    private void SetPersistedDuration(Guid movieProjectId, int durationSeconds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var movie = db.MovieProjects.Single(item => item.Id == movieProjectId);
        movie.DurationSeconds = durationSeconds;
        movie.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    private async Task<int> ReadPersistedDurationAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieProjects.AsNoTracking()
            .Where(item => item.Id == movieProjectId)
            .Select(item => item.DurationSeconds)
            .SingleAsync();
    }

    private void AssertNoGenerationSideEffects(Guid workspaceId, Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        Assert.False(db.GenerationJobs.AsNoTracking().Any(job => job.WorkspaceId == workspaceId));
        Assert.False(db.UsageTransactions.AsNoTracking().Any(transaction => transaction.WorkspaceId == workspaceId));
        Assert.False(db.MovieClips.AsNoTracking().Any(clip => clip.MovieProjectId == movieProjectId));
    }
}
