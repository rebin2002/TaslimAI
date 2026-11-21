using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Project-scoped authorization boundary for Movie timeline clip edits. The transition-edit
/// path is the one clip-edit mutation that enforces the Movie Edit authority through the
/// fail-closed collaboration guard, so a caller without that authority must receive a
/// deterministic permission denial rather than an unhandled server error, and nothing may be
/// attempted, materialized, version-bumped or persisted against another user's timeline.
///
/// Provider-free, spend-free and data-free: real controller pipeline, CSRF, in-memory SQLite,
/// synthetic fixtures only.
/// </summary>
public sealed class MovieTimelineClipEditAuthorizationTests : IClassFixture<TaslimApiFactory>, IDisposable
{
    private readonly TaslimApiFactory factory;
    private readonly List<HttpClient> clients = [];

    public MovieTimelineClipEditAuthorizationTests(TaslimApiFactory factory)
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
    public async Task TimelineClipEditWithoutProjectEditAuthorityIsDeniedAndPersistsNothing()
    {
        var world = await CreateWorldAsync("clip-edit-authority");

        // A workspace member with an explicit Edit deny override has no project Edit authority.
        using var response = await SendTransitionEditAsync(world.Viewer, world.MovieProjectId, world.TimelineId, world.ShotIds);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_PERMISSION_DENIED", body.GetProperty("error").GetProperty("code").GetString());

        // Denied before anything was attempted: no edit row, no canonical version movement.
        Assert.Equal(0, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineVersionAsync(world.TimelineId));
    }

    [Fact]
    public async Task TimelineClipEditByNonMemberIsDeniedAndPersistsNothing()
    {
        var world = await CreateWorldAsync("clip-edit-nonmember");

        using var response = await SendTransitionEditAsync(world.Outsider, world.MovieProjectId, world.TimelineId, world.ShotIds);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineVersionAsync(world.TimelineId));
    }

    [Fact]
    public async Task TimelineClipEditFromAnotherWorkspaceIsDeniedWithoutLeakingProjectDetail()
    {
        var world = await CreateWorldAsync("clip-edit-foreign");

        // A wrong-workspace caller has no team membership at all, so the fail-closed guard
        // denies them with the same indistinguishable project-scoped 403 that a non-member
        // receives. The generic denial message carries no detail about another workspace's
        // project, and nothing is read into or written back to that project's timeline.
        using var response = await SendTransitionEditAsync(world.ForeignWorkspaceUser, world.MovieProjectId, world.TimelineId, world.ShotIds);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_PERMISSION_DENIED", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineVersionAsync(world.TimelineId));
    }

    [Fact]
    public async Task TimelineClipEditByEditCapableMemberSucceedsAndRevocationStopsFurtherEdits()
    {
        var world = await CreateWorldAsync("clip-edit-allowed");

        using var allowed = await SendTransitionEditAsync(world.Editor, world.MovieProjectId, world.TimelineId, world.ShotIds);
        var allowedBody = await allowed.Content.ReadAsStringAsync();
        Assert.True(allowed.StatusCode is HttpStatusCode.OK, allowedBody);
        Assert.Equal(2, ReadResultVersion(allowedBody));
        Assert.Equal(1, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineVersionAsync(world.TimelineId));

        // Revoking the project Edit authority takes effect on the very next attempt.
        await SetEditOverrideAsync(world.EditorId, world.MovieProjectId, granted: false);

        using var revoked = await SendTransitionEditAsync(
            world.Editor,
            world.MovieProjectId,
            world.TimelineId,
            world.ShotIds,
            baseVersion: 2);

        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(1, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineVersionAsync(world.TimelineId));
    }

    private sealed record World(
        HttpClient Owner,
        HttpClient Viewer,
        HttpClient Editor,
        HttpClient Outsider,
        HttpClient ForeignWorkspaceUser,
        Guid ViewerId,
        Guid EditorId,
        Guid MovieProjectId,
        Guid TimelineId,
        IReadOnlyList<Guid> ShotIds);

    [Fact]
    public async Task RevisionScopedClipEditByViewOnlyMemberFailsClosedWithoutMaterializingARevision()
    {
        var world = await CreateWorldAsync("clip-edit-revision-denied");
        var (revisionId, canonical) = await SeedRevisionWithCanonicalSnapshotAsync(world);
        var request = BuildRevisionTransitionEditRequest(revisionId, canonical, canonical.Clips[0].ClipId);

        using var response = await SendWithCsrfAsync(
            world.Viewer, HttpMethod.Post, $"/api/movie-studio/projects/{world.MovieProjectId}/timeline/transitions", request);

        // The sibling clip-edit path returns null on denial and the endpoint surfaces the
        // established project-scoped 404: no existence detail, no attempted materialization.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_TIMELINE_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(await RevisionExistsAsync(world.TimelineId, 2));
        Assert.Equal(1, await ReadTimelineVersionAsync(world.TimelineId));
    }

    [Fact]
    public async Task RevisionScopedClipEditByEditCapableMemberMaterializesExactlyOneRevision()
    {
        var world = await CreateWorldAsync("clip-edit-revision-allowed");
        var (revisionId, canonical) = await SeedRevisionWithCanonicalSnapshotAsync(world);
        var request = BuildRevisionTransitionEditRequest(revisionId, canonical, canonical.Clips[0].ClipId);

        using var response = await SendWithCsrfAsync(
            world.Editor, HttpMethod.Post, $"/api/movie-studio/projects/{world.MovieProjectId}/timeline/transitions", request);

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.OK, text);
        Assert.True(await RevisionExistsAsync(world.TimelineId, 2));
        Assert.Equal(2, await ReadTimelineVersionAsync(world.TimelineId));
    }

    private async Task<World> CreateWorldAsync(string prefix)
    {
        var owner = CreateClient();
        var ownerAuth = await RegisterAsync(owner, $"{prefix} owner");

        var viewer = CreateClient();
        var viewerAuth = await RegisterAsync(viewer, $"{prefix} viewer");

        var editor = CreateClient();
        var editorAuth = await RegisterAsync(editor, $"{prefix} editor");

        var outsider = CreateClient();
        var outsiderAuth = await RegisterAsync(outsider, $"{prefix} outsider");

        var foreign = CreateClient();
        var foreignAuth = await RegisterAsync(foreign, $"{prefix} foreign");

        var now = DateTime.UtcNow;
        var movieProjectId = Guid.NewGuid();
        var timelineId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var clipIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

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
                Description = "Provider-free timeline clip-edit authorization fixture.",
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
            var editorMemberId = Guid.NewGuid();
            db.MovieTeamMembers.Add(new MovieTeamMember
            {
                Id = viewerMemberId, MovieProjectId = movieProjectId, UserId = viewerAuth.User.Id,
                Role = MovieTeamRoles.Writer, IsProjectOwner = false, CreatedAt = now, UpdatedAt = now,
            });
            db.MovieTeamMembers.Add(new MovieTeamMember
            {
                Id = editorMemberId, MovieProjectId = movieProjectId, UserId = editorAuth.User.Id,
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
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(), WorkspaceId = ownerAuth.PersonalWorkspace.Id, UserId = editorAuth.User.Id,
                Role = WorkspaceRole.Member, JoinedAt = now,
            });

            await db.SaveChangesAsync();
        }

        return new World(
            owner, viewer, editor, outsider, foreign,
            viewerAuth.User.Id, editorAuth.User.Id, movieProjectId, timelineId, shotIds);
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        clients.Add(client);
        return client;
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

    private static Taslim.Api.Contracts.MovieTimelineTransitionEditRequest BuildRevisionTransitionEditRequest(
        Guid baseRevisionId,
        MovieCanonicalTimelineContract canonical,
        Guid incomingClipId)
    {
        var proposed = new MovieTimelineTransitionContract(
            Guid.NewGuid(), MovieTimelineTransitionTypes.FadeIn, null, incomingClipId,
            canonical.Clips[0].StartSeconds, 1m);
        return new Taslim.Api.Contracts.MovieTimelineTransitionEditRequest
        {
            BaseRevisionId = baseRevisionId,
            Decision = new MovieTimelineEditDecisionContract(
                DecisionId: Guid.NewGuid(),
                TimelineId: canonical.TimelineId,
                BaseTimelineVersion: canonical.Version,
                Action: MovieTimelineEditActions.Add,
                TransitionId: null,
                ProposedTransition: proposed,
                DirectorRecommendation: null,
                UserOverride: new MovieUserTransitionOverrideContract(
                    OverrideId: Guid.NewGuid(),
                    TimelineId: canonical.TimelineId,
                    TimelineVersion: canonical.Version,
                    TransitionId: null,
                    ProposedTransition: proposed,
                    Reason: "Synthetic revision-scoped transition edit.")),
        };
    }

    private async Task<(Guid RevisionId, MovieCanonicalTimelineContract Canonical)> SeedRevisionWithCanonicalSnapshotAsync(World world)
    {
        var revisionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var canonical = BuildTransitionEditRequest(world.TimelineId, world.ShotIds, 1).Timeline;
        db.MovieTimelineRevisions.Add(new MovieTimelineRevision
        {
            Id = revisionId,
            MovieTimelineId = world.TimelineId,
            RevisionNumber = 1,
            Status = MovieTimelineRevisionStatuses.Draft,
            Label = "Authorization fixture revision",
            CanonicalTimelineJson = JsonSerializer.Serialize(canonical, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            DurationMilliseconds = 8_000,
            CreatedByUserId = world.ViewerId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        var timeline = await db.MovieTimelines.SingleAsync(item => item.Id == world.TimelineId);
        timeline.CurrentRevisionId = revisionId;
        await db.SaveChangesAsync();
        return (revisionId, canonical);
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
                    Reason: "Synthetic fade-in applied through the authorization boundary test.")),
        };
    }

    private static int ReadResultVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("resultTimelineVersion").GetInt32();
    }

    private async Task<int> CountEditsAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineTransitionEdits.AsNoTracking().CountAsync(item => item.MovieTimelineId == timelineId);
    }

    private async Task<int> ReadTimelineVersionAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelines.AsNoTracking()
            .Where(item => item.Id == timelineId)
            .Select(item => item.CurrentRevisionNumber)
            .SingleAsync();
    }

    private async Task<bool> RevisionExistsAsync(Guid timelineId, int revisionNumber)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineRevisions.AsNoTracking()
            .AnyAsync(item => item.MovieTimelineId == timelineId && item.RevisionNumber == revisionNumber);
    }

    private async Task SetEditOverrideAsync(Guid userId, Guid movieProjectId, bool granted)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var member = await db.MovieTeamMembers.SingleAsync(item => item.MovieProjectId == movieProjectId && item.UserId == userId);
        var existing = await db.MovieTeamMemberPermissions
            .FirstOrDefaultAsync(item => item.MovieTeamMemberId == member.Id && item.Permission == MoviePermissions.Edit);
        if (existing is null)
            db.MovieTeamMemberPermissions.Add(new MovieTeamMemberPermission { Id = Guid.NewGuid(), MovieTeamMemberId = member.Id, Permission = MoviePermissions.Edit, Granted = granted });
        else
            existing.Granted = granted;
        await db.SaveChangesAsync();
    }

    private async Task<AuthResponse> RegisterAsync(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"timeline-clip-edit-{Guid.NewGuid():N}@example.com",
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
