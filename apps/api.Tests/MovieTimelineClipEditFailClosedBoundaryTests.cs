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
/// Complementary fail-closed boundary coverage for the Movie timeline clip-edit
/// authorization contract introduced by PR #965. The six tests in
/// <see cref="MovieTimelineClipEditAuthorizationTests"/> pin the denial of real
/// project states (deny-override member, non-member, wrong-workspace caller,
/// post-revocation attempt, authorized success and the revision-scoped siblings).
///
/// This class pins the two fail-closed edges that remain unasserted there:
///
/// 1. A transition-edit POST against a MOVIE PROJECT ID THAT DOES NOT EXIST must
///    fail closed through the same sanitized, generic 403 MOVIE_PERMISSION_DENIED
///    as a denied member probe on a real project - byte-identical envelope, never
///    a 500 and never an existence-revealing 404 - and must persist no edit row.
///    That indistinguishability is the enumeration-neutral property the #965 fix
///    promises ("the same indistinguishable project-scoped 403"), but the existing
///    tests only assert the status code and error code, not the sanitized body.
///
/// 2. Authorization must take PRECEDENCE over payload validation: a caller whose
///    Edit authority was revoked while submitting a request that would otherwise
///    trip the stale-version conflict branch must still receive 403 (not 409,
///    not 500), with no additional edit row and no canonical revision movement.
///    The existing post-revocation test sends a currently-consistent baseVersion,
///    so the guard-vs-validation ordering itself is never exercised.
///
/// Provider-free, spend-free and data-free: real controller pipeline, CSRF,
/// in-memory SQLite, synthetic fixtures only - the same discipline as #965.
/// </summary>
public sealed class MovieTimelineClipEditFailClosedBoundaryTests : IClassFixture<TaslimApiFactory>, IDisposable
{
    private const string GenericDenialMessage = "You do not have permission to perform this action.";

    private readonly TaslimApiFactory factory;
    private readonly List<HttpClient> clients = [];

    public MovieTimelineClipEditFailClosedBoundaryTests(TaslimApiFactory factory)
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
    public async Task TransitionEditAgainstUnknownProjectFailsClosedAsIndistinguishableSanitized403()
    {
        var world = await CreateWorldAsync("clip-edit-failclosed-unknown-project");

        // Baseline denial on a REAL project: a member whose Edit authority is denied.
        using var realProjectDenial = await SendTransitionEditAsync(
            world.Viewer, world.MovieProjectId, world.TimelineId, world.ShotIds);
        Assert.Equal(HttpStatusCode.Forbidden, realProjectDenial.StatusCode);
        var realBody = await realProjectDenial.Content.ReadAsStringAsync();

        // The probe: the same caller posts a well-formed transition edit against a
        // project id that does not exist anywhere. The fail-closed guard denies it
        // before any timeline row is read, so the response must be indistinguishable
        // from the real-project denial - no 500, no existence-revealing 404.
        var ghostProjectId = Guid.NewGuid();
        var ghostTimelineId = Guid.NewGuid();
        using var ghostDenial = await SendTransitionEditAsync(
            world.Viewer, ghostProjectId, ghostTimelineId, world.ShotIds);
        var ghostBody = await ghostDenial.Content.ReadAsStringAsync();

        // Never a 500 and never a 404: a sanitized 403 with the established code.
        Assert.Equal(HttpStatusCode.Forbidden, ghostDenial.StatusCode);
        var ghostJson = JsonDocument.Parse(ghostBody).RootElement.GetProperty("error");
        Assert.Equal("MOVIE_PERMISSION_DENIED", ghostJson.GetProperty("code").GetString());

        // Sanitized: the generic guard message only, and no identifier, project
        // title or exception-type detail anywhere in the serialized envelope.
        Assert.Equal(GenericDenialMessage, ghostJson.GetProperty("message").GetString());
        foreach (var leaked in new[]
                 {
                     ghostProjectId.ToString(), ghostTimelineId.ToString(),
                     world.MovieProjectId.ToString(), world.TimelineId.ToString(),
                     "MovieCollaborationForbiddenException", "Stack", "at Taslim.",
                 })
        {
            Assert.DoesNotContain(leaked, ghostBody, StringComparison.OrdinalIgnoreCase);
        }

        // Enumeration-neutral: the unknown-project denial is byte-identical to the
        // real-project permission denial, so probing cannot distinguish the two.
        Assert.Equal(realBody, ghostBody);

        // Nothing persisted for the unknown project or its (nonexistent) timeline.
        Assert.Equal(0, await CountEditsForProjectAsync(ghostProjectId));
        Assert.Equal(0, await CountEditsForTimelineAsync(ghostTimelineId));
    }

    [Fact]
    public async Task RevokedEditAuthorityDeniesBeforeStaleVersionConflictAndPersistsNothing()
    {
        var world = await CreateWorldAsync("clip-edit-failclosed-revoked-stale");

        // The authorized branch still works: the first edit succeeds and persists
        // exactly one row, moving the persisted result chain to version 2 while
        // the canonical timeline revision number stays at its seed value of 1.
        using var allowed = await SendTransitionEditAsync(
            world.Editor, world.MovieProjectId, world.TimelineId, world.ShotIds, baseVersion: 1);
        var allowedBody = await allowed.Content.ReadAsStringAsync();
        Assert.True(allowed.StatusCode is HttpStatusCode.OK, allowedBody);
        Assert.Equal(2, ReadResultVersion(allowedBody));
        Assert.Equal(1, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineVersionAsync(world.TimelineId));

        // Revoke the project Edit authority outright.
        await SetEditOverrideAsync(world.EditorId, world.MovieProjectId, granted: false);

        // The uncovered ordering edge: resubmit with a STALE baseVersion (1) that
        // would otherwise trip the TimelineVersionConflict branch (409) for an
        // authorized caller. The fail-closed guard runs first, so the revoked
        // caller must receive the sanitized 403 - never 409, never 500.
        using var revoked = await SendTransitionEditAsync(
            world.Editor, world.MovieProjectId, world.TimelineId, world.ShotIds, baseVersion: 1);
        var revokedBody = await revoked.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        var error = JsonDocument.Parse(revokedBody).RootElement.GetProperty("error");
        Assert.Equal("MOVIE_PERMISSION_DENIED", error.GetProperty("code").GetString());
        Assert.Equal(GenericDenialMessage, error.GetProperty("message").GetString());

        // The denied attempt persisted nothing and moved nothing: still the single
        // row from the authorized phase and no canonical revision increment.
        Assert.Equal(1, await CountEditsAsync(world.TimelineId));
        Assert.Equal(1, await ReadTimelineVersionAsync(world.TimelineId));
    }

    private sealed record World(
        HttpClient Owner,
        HttpClient Viewer,
        HttpClient Editor,
        Guid ViewerId,
        Guid EditorId,
        Guid MovieProjectId,
        Guid TimelineId,
        IReadOnlyList<Guid> ShotIds);

    private async Task<World> CreateWorldAsync(string prefix)
    {
        var owner = CreateClient();
        var ownerAuth = await RegisterAsync(owner, $"{prefix} owner");

        var viewer = CreateClient();
        var viewerAuth = await RegisterAsync(viewer, $"{prefix} viewer");

        var editor = CreateClient();
        var editorAuth = await RegisterAsync(editor, $"{prefix} editor");

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
                Description = "Provider-free fail-closed timeline clip-edit boundary fixture.",
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
            owner, viewer, editor,
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
        return await SendWithCsrfAsync(
            client, HttpMethod.Post, $"/api/movie-studio/projects/{movieProjectId}/timeline/transition-edits", request);
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
                    Reason: "Synthetic fade-in applied through the fail-closed boundary test.")),
        };
    }

    private static int ReadResultVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("resultTimelineVersion").GetInt32();
    }

    private async Task<int> CountEditsAsync(Guid timelineId) => await CountEditsForTimelineAsync(timelineId);

    private async Task<int> CountEditsForTimelineAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineTransitionEdits.AsNoTracking().CountAsync(item => item.MovieTimelineId == timelineId);
    }

    private async Task<int> CountEditsForProjectAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineTransitionEdits.AsNoTracking().CountAsync(item => item.MovieProjectId == movieProjectId);
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
            email = $"clip-edit-failclosed-{Guid.NewGuid():N}@example.com",
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
