using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Request-level denial snapshot boundary for the project-scoped Movie timeline
/// transition-edit endpoint (#965 lane QA). A caller without the Movie Edit authority
/// receives a deterministic 403 MOVIE_PERMISSION_DENIED from the POST, and the persisted
/// timeline snapshot must then be observable, through the snapshot read endpoints
/// themselves, as byte-identical to the pre-denial state: the denial leaves no stored
/// transition-edit snapshot (the snapshot read stays the established 404
/// MOVIE_TIMELINE_TRANSITIONS_NOT_FOUND), no edit row, no canonical revision movement,
/// and repeating the read after the denial is deterministic rather than order- or
/// cache-dependent.
///
/// Complementary to, and not duplicating, the sibling lanes:
/// * #965's MovieTimelineClipEditAuthorizationTests pins the denial POST and the
///   database-side absence of rows/versions via direct DbContext counts.
/// * #1075's MovieTimelineClipEditFailClosedBoundaryTests pins the unknown-project
///   403 shape and authorization-over-validation precedence.
/// * #1099's MovieTimelineLateRevocationProjectScopeTests pins late-revocation denial
///   shape and project scope with database-side chain assertions.
/// None of them performs the read-after-denial through the snapshot GET endpoints, so
/// the request-level snapshot immutability pinned here was uncovered.
///
/// Provider-free, spend-free and data-free: real controller pipeline, CSRF, in-memory
/// SQLite, synthetic fixtures only, no network, no timing dependence, no reservation,
/// no charge.
/// </summary>
public sealed class MovieTimelineTransitionEditDenialSnapshotTests : IClassFixture<TaslimApiFactory>, IDisposable
{
    private readonly TaslimApiFactory factory;
    private readonly List<HttpClient> clients = [];

    public MovieTimelineTransitionEditDenialSnapshotTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var client in clients)
            client.Dispose();
        clients.Clear();
    }

    [Fact]
    public async Task DeniedTransitionEditLeavesSnapshotReadIdenticalAndNoStoredSnapshot()
    {
        var world = await CreateWorldAsync("denial-snapshot-viewer");

        // The snapshot the project members observe before any denial.
        var ownerBefore = await ReadSnapshotAsync(world.Owner, world.MovieProjectId);
        var viewerBefore = await ReadSnapshotAsync(world.Viewer, world.MovieProjectId);
        Assert.True(((int)ownerBefore.Status >= 200 && (int)ownerBefore.Status < 300), ownerBefore.Body);
        Assert.True(((int)viewerBefore.Status >= 200 && (int)viewerBefore.Status < 300), viewerBefore.Body);
        Assert.Equal(ownerBefore.Body, viewerBefore.Body);
        await AssertNoStoredSnapshotAsync(world.Viewer, world.MovieProjectId);

        // The explicit Edit-deny override member is refused at request level.
        using var denied = await SendTransitionEditAsync(world.Viewer, world.MovieProjectId, world.TimelineId, world.ShotIds);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var denialBody = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_PERMISSION_DENIED", denialBody.GetProperty("error").GetProperty("code").GetString());

        // Read-after-denial through the snapshot endpoints: the owner's timeline view is
        // byte-identical to the pre-denial state - no version movement, no new revision,
        // no mutation of any snapshot column - and the transition-edit snapshot store is
        // still empty (404), so the denial persisted nothing observable.
        var ownerAfter = await ReadSnapshotAsync(world.Owner, world.MovieProjectId);
        Assert.Equal(ownerBefore.Body, ownerAfter.Body);
        await AssertNoStoredSnapshotAsync(world.Viewer, world.MovieProjectId);
    }

    [Fact]
    public async Task SnapshotReadAfterDenialIsDeterministicAcrossRepeatReadsAndPrincipals()
    {
        var world = await CreateWorldAsync("denial-snapshot-repeat");

        var ownerBefore = await ReadSnapshotAsync(world.Owner, world.MovieProjectId);

        using var denied = await SendTransitionEditAsync(world.Viewer, world.MovieProjectId, world.TimelineId, world.ShotIds);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Repeating the read is deterministic: same principal gets the identical snapshot
        // every time, the denied principal sees that same unchanged snapshot, and the
        // snapshot store remains uniformly absent for every read order - never a partial
        // or denial-shaped artifact.
        var first = await ReadSnapshotAsync(world.Owner, world.MovieProjectId);
        var second = await ReadSnapshotAsync(world.Owner, world.MovieProjectId);
        var viaDeniedMember = await ReadSnapshotAsync(world.Viewer, world.MovieProjectId);
        Assert.Equal(ownerBefore.Body, first.Body);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(first.Body, viaDeniedMember.Body);

        var snapshotFirst = await ReadStoredSnapshotAsync(world.Viewer, world.MovieProjectId);
        var snapshotSecond = await ReadStoredSnapshotAsync(world.Owner, world.MovieProjectId);
        Assert.Equal(HttpStatusCode.NotFound, snapshotFirst.Status);
        Assert.Equal(HttpStatusCode.NotFound, snapshotSecond.Status);
        Assert.Equal(snapshotFirst.Body, snapshotSecond.Body);
        Assert.Equal(
            "MOVIE_TIMELINE_TRANSITIONS_NOT_FOUND",
            JsonDocument.Parse(snapshotFirst.Body).RootElement.GetProperty("error").GetProperty("code").GetString());

        // And the storage side agrees with the request-level observation.
        Assert.Equal(0, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineRevisionNumberAsync(world.TimelineId));
        Assert.Equal(0, await CountRevisionsAsync(world.TimelineId));
    }

    [Fact]
    public async Task DeniedTransitionEditByNonMemberKeepsSnapshotUnreadableAndOwnerSnapshotUnchanged()
    {
        var world = await CreateWorldAsync("denial-snapshot-nonmember");

        var ownerBefore = await ReadSnapshotAsync(world.Owner, world.MovieProjectId);
        Assert.True(((int)ownerBefore.Status >= 200 && (int)ownerBefore.Status < 300), ownerBefore.Body);

        // A non-member cannot observe the snapshot before the attempt...
        var outsiderBefore = await ReadSnapshotAsync(world.Outsider, world.MovieProjectId);
        Assert.Equal(HttpStatusCode.NotFound, outsiderBefore.Status);

        // ...is refused with the project-scoped 403 when trying to write one...
        using var denied = await SendTransitionEditAsync(world.Outsider, world.MovieProjectId, world.TimelineId, world.ShotIds);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // ...still cannot read it afterwards, and reading it never degrades into an
        // authorization leak or a denial-shaped 403 on the GET path.
        var outsiderAfter = await ReadSnapshotAsync(world.Outsider, world.MovieProjectId);
        Assert.Equal(HttpStatusCode.NotFound, outsiderAfter.Status);
        Assert.Equal(outsiderBefore.Body, outsiderAfter.Body);
        Assert.Equal(
            "MOVIE_TIMELINE_NOT_FOUND",
            JsonDocument.Parse(outsiderAfter.Body).RootElement.GetProperty("error").GetProperty("code").GetString());

        // The owner's snapshot is byte-identical: the unauthorised write attempt changed
        // nothing observable, persisted nothing, and bumped nothing.
        var ownerAfter = await ReadSnapshotAsync(world.Owner, world.MovieProjectId);
        Assert.Equal(ownerBefore.Body, ownerAfter.Body);
        await AssertNoStoredSnapshotAsync(world.Owner, world.MovieProjectId);
        Assert.Equal(0, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineRevisionNumberAsync(world.TimelineId));
    }

    private sealed record World(
        HttpClient Owner,
        HttpClient Viewer,
        HttpClient Outsider,
        Guid MovieProjectId,
        Guid TimelineId,
        IReadOnlyList<Guid> ShotIds);

    private readonly record struct ReadResult(HttpStatusCode Status, string Body);

    private async Task<World> CreateWorldAsync(string prefix)
    {
        var owner = CreateClient();
        var ownerAuth = await RegisterAsync(owner, $"{prefix} owner");

        var viewer = CreateClient();
        var viewerAuth = await RegisterAsync(viewer, $"{prefix} viewer");

        var outsider = CreateClient();
        var outsiderAuth = await RegisterAsync(outsider, $"{prefix} outsider");

        var now = DateTime.UtcNow;
        var movieProjectId = Guid.NewGuid();
        var timelineId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

            db.MovieProjects.Add(new MovieProject
            {
                Id = movieProjectId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                CreatedByUserId = ownerAuth.User.Id,
                Mode = MovieProjectModes.Full,
                Title = $"{prefix} feature",
                Description = "Provider-free denial-snapshot read fixture.",
                DurationSeconds = 120,
                Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.MovieScenes.Add(new MovieScene
            {
                Id = sceneId, MovieProjectId = movieProjectId, Sequence = 1,
                Title = "Opening", Summary = "A synthetic opening scene.", CreatedAt = now, UpdatedAt = now,
            });
            foreach (var (shotId, sequence) in shotIds.Select((id, index) => (id, index + 1)))
            {
                db.MovieShots.Add(new MovieShot
                {
                    Id = shotId, MovieSceneId = sceneId, Sequence = sequence,
                    Description = $"Synthetic shot {sequence}.", DurationSeconds = 4, CreatedAt = now, UpdatedAt = now,
                });
            }
            db.MovieTimelines.Add(new MovieTimeline
            {
                Id = timelineId, MovieProjectId = movieProjectId, CurrentRevisionNumber = 1,
                CreatedAt = now, UpdatedAt = now,
            });

            db.MovieTeamMembers.Add(new MovieTeamMember
            {
                Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = ownerAuth.User.Id,
                Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now,
            });

            var viewerMemberId = Guid.NewGuid();
            db.MovieTeamMembers.Add(new MovieTeamMember
            {
                Id = viewerMemberId, MovieProjectId = movieProjectId, UserId = viewerAuth.User.Id,
                Role = MovieTeamRoles.Writer, IsProjectOwner = false, CreatedAt = now, UpdatedAt = now,
            });
            db.MovieTeamMemberPermissions.AddRange(
                new MovieTeamMemberPermission { Id = Guid.NewGuid(), MovieTeamMemberId = viewerMemberId, Permission = MoviePermissions.View, Granted = true },
                new MovieTeamMemberPermission { Id = Guid.NewGuid(), MovieTeamMemberId = viewerMemberId, Permission = MoviePermissions.Edit, Granted = false });

            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(), WorkspaceId = ownerAuth.PersonalWorkspace.Id, UserId = viewerAuth.User.Id,
                Role = WorkspaceRole.Member, JoinedAt = now,
            });

            await db.SaveChangesAsync();
        }

        return new World(owner, viewer, outsider, movieProjectId, timelineId, shotIds);
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        clients.Add(client);
        return client;
    }

    private async Task<ReadResult> ReadSnapshotAsync(HttpClient client, Guid movieProjectId)
    {
        using var response = await client.GetAsync($"/api/movie-studio/projects/{movieProjectId}/timeline");
        return new(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<ReadResult> ReadStoredSnapshotAsync(HttpClient client, Guid movieProjectId)
    {
        using var response = await client.GetAsync($"/api/movie-studio/projects/{movieProjectId}/timeline/transition-edits");
        return new(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task AssertNoStoredSnapshotAsync(HttpClient client, Guid movieProjectId)
    {
        var read = await ReadStoredSnapshotAsync(client, movieProjectId);
        Assert.Equal(HttpStatusCode.NotFound, read.Status);
        Assert.Equal(
            "MOVIE_TIMELINE_TRANSITIONS_NOT_FOUND",
            JsonDocument.Parse(read.Body).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private async Task<HttpResponseMessage> SendTransitionEditAsync(
        HttpClient client,
        Guid movieProjectId,
        Guid timelineId,
        IReadOnlyList<Guid> shotIds,
        int baseVersion = 1)
    {
        var request = BuildTransitionEditRequest(timelineId, shotIds, baseVersion);
        return await SendWithCsrfAsync(client, HttpMethod.Post, $"/api/movie-studio/projects/{movieProjectId}/timeline/transition-edits", request);
    }

    private static Taslim.Api.Movies.MovieTimelineTransitionEditRequest BuildTransitionEditRequest(Guid timelineId, IReadOnlyList<Guid> shotIds, int baseVersion)
    {
        var clipIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var clips = new List<MovieTimelineClipContract>();
        var start = 0m;
        for (var index = 0; index < shotIds.Count; index++)
        {
            clips.Add(new MovieTimelineClipContract(clipIds[index], shotIds[index], index + 1, start, 4m));
            start += 4m;
        }

        var timeline = new MovieCanonicalTimelineContract(timelineId, baseVersion, clips, Array.Empty<MovieTimelineTransitionContract>());
        var proposed = new MovieTimelineTransitionContract(Guid.NewGuid(), MovieTimelineTransitionTypes.FadeIn, null, clipIds[0], 0m, 1m);

        return new Taslim.Api.Movies.MovieTimelineTransitionEditRequest
        {
            Timeline = timeline,
            Decision = new MovieTimelineEditDecisionContract(
                DecisionId: Guid.NewGuid(),
                TimelineId: timelineId,
                BaseTimelineVersion: baseVersion,
                Action: MovieTimelineEditActions.Add,
                TransitionId: null,
                ProposedTransition: proposed,
                DirectorRecommendation: null,
                UserOverride: new MovieUserTransitionOverrideContract(
                    OverrideId: Guid.NewGuid(),
                    TimelineId: timelineId,
                    TimelineVersion: baseVersion,
                    TransitionId: null,
                    ProposedTransition: proposed,
                    Reason: "Synthetic denial-snapshot read probe.")),
        };
    }

    private async Task<int> CountEditsAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineTransitionEdits.AsNoTracking().CountAsync(item => item.MovieTimelineId == timelineId);
    }

    private async Task<int> CountRevisionsAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineRevisions.AsNoTracking().CountAsync(item => item.MovieTimelineId == timelineId);
    }

    private async Task<int> ReadTimelineRevisionNumberAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelines.AsNoTracking()
            .Where(item => item.Id == timelineId)
            .Select(item => item.CurrentRevisionNumber)
            .SingleAsync();
    }

    private async Task<AuthResponse> RegisterAsync(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"denial-snapshot-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
