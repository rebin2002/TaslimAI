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
/// Late permission-revocation edges for the Movie Studio timeline transition-edit endpoint
/// (<c>POST /api/movie-studio/projects/{id}/timeline/transition-edits</c>).
///
/// PR #965 adds the sanitized <c>403 MOVIE_PERMISSION_DENIED</c> mapping for this endpoint and
/// pins six outcomes: a denied-override member, a non-member, a wrong-workspace caller, an
/// authorized edit followed by flipping the <c>Edit</c> override off, and the two
/// revision-scoped siblings. PR #1075 additionally pins the unknown-project indistinguishable
/// 403 and 403-before-409 precedence. What no test in the repository asserts is *how* a former
/// editor loses that authority after the fact, and whether the loss stays inside one project.
/// A repository-wide search found no test that deletes a <c>MovieTeamMember</c> row and none that
/// removes a movie workspace's <c>WorkspaceMember</c> row, which is exactly the shape of a real
/// revocation. These three tests cover it.
///
/// 1. Revocation by <b>team removal</b>. Every supported Movie role grants <c>Edit</c>, so no
///    role change can revoke it; removing the membership row is the only membership-shaped
///    revocation, and it is denied in a different branch of
///    <see cref="MovieCollaborationAccess.GetMemberAsync"/> than an override deny - the lookup
///    returns <c>null</c> instead of a member whose override says no. The next attempt must be a
///    403 and the already-persisted chain must not move.
/// 2. Revocation by <b>workspace removal while the team row is left dangling</b>. The stale
///    <c>MovieTeamMember</c> still nominally grants <c>Edit</c> through the Writer role, so only
///    the fail-closed workspace fence can deny the caller. If that fence were bypassed, a removed
///    workspace member would keep editing another workspace's timelines: the security-relevant
///    edge of the trio, and it must deny every project in the workspace, not just one.
/// 3. Revocation <b>scoping</b>. Losing <c>Edit</c> on one project must deny only that project
///    while a sibling project in the same workspace stays editable. Both directions pin that a
///    denial has no cross-project side effect.
///
/// Each test asserts the whole fail-closed contract a visible denial promises: HTTP 403 (never a
/// raw exception, never a 5xx, and never a 200), no clip-edit row for the denied target, no
/// canonical timeline revision movement, no new revision row, an untouched project row, and no
/// mutation of any other project - including a bystander's project in another workspace.
///
/// Provider-free, spend-free and data-free: real controller pipeline with CSRF, in-memory
/// SQLite, synthetic fixtures only, no network, no timing dependence, no reservation, no charge.
/// </summary>
public sealed class MovieTimelineLateRevocationProjectScopeTests : IClassFixture<TaslimApiFactory>, IDisposable
{
    private const string GenericDenialMessage = "You do not have permission to perform this action.";

    private readonly TaslimApiFactory factory;
    private readonly List<HttpClient> clients = [];

    public MovieTimelineLateRevocationProjectScopeTests(TaslimApiFactory factory)
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
    public async Task LateTeamRemovalDeniesTheNextTransitionEditWithoutMovingThePersistedChain()
    {
        var world = await CreateWorldAsync("late-rev-team");

        // Formerly an editor: Edit derived from the Writer role, with no override rows at all.
        using var before = await SendTransitionEditAsync(world.Editor, world.ProjectA, world.TimelineA, world.ShotsA);
        var beforeBody = await before.Content.ReadAsStringAsync();
        Assert.True(before.StatusCode is HttpStatusCode.OK, beforeBody);
        Assert.Equal(2, ReadResultVersion(beforeBody));
        Assert.Equal(1, await CountEditsForProjectAsync(world.ProjectA));

        var chainBefore = await ReadLatestResultVersionAsync(world.TimelineA);
        var revisionBefore = await ReadTimelineRevisionNumberAsync(world.TimelineA);
        var projectStampBefore = await ReadProjectUpdatedAtAsync(world.ProjectA);

        // Late revocation: the user is taken off this movie team entirely.
        await RemoveTeamMembershipAsync(world.EditorId, world.ProjectA);

        using var after = await SendTransitionEditAsync(world.Editor, world.ProjectA, world.TimelineA, world.ShotsA, baseVersion: 2);
        var afterBody = await after.Content.ReadAsStringAsync();

        // A permission decision, not an unhandled fault.
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.True((int)after.StatusCode is >= 400 and < 500, afterBody);
        AssertJsonDenial(afterBody);

        // The denied attempt mutated nothing: no new clip row, chain frozen, no revision, project untouched.
        Assert.Equal(1, await CountEditsForProjectAsync(world.ProjectA));
        Assert.Equal(1, await CountEditsForTimelineAsync(world.TimelineA));
        Assert.Equal(chainBefore, await ReadLatestResultVersionAsync(world.TimelineA));
        Assert.Equal(revisionBefore, await ReadTimelineRevisionNumberAsync(world.TimelineA));
        Assert.Equal(0, await CountRevisionsAsync(world.TimelineA));
        Assert.Equal(projectStampBefore, await ReadProjectUpdatedAtAsync(world.ProjectA));

        // No cross-project side effect: the sibling project and the other workspace are untouched.
        Assert.Equal(0, await CountEditsForProjectAsync(world.ProjectB));
        Assert.Equal(revisionBefore, await ReadTimelineRevisionNumberAsync(world.TimelineB));
        Assert.Equal(0, await CountEditsForProjectAsync(world.ProjectC));
    }

    [Fact]
    public async Task LateWorkspaceRemovalWithStaleTeamGrantFailsClosedAndGrantsNothingAnywhere()
    {
        var world = await CreateWorldAsync("late-rev-workspace");

        // Formerly an editor: the request succeeds while the workspace fence holds.
        using var before = await SendTransitionEditAsync(world.Editor, world.ProjectA, world.TimelineA, world.ShotsA);
        var beforeBody = await before.Content.ReadAsStringAsync();
        Assert.True(before.StatusCode is HttpStatusCode.OK, beforeBody);
        Assert.Equal(1, await CountEditsForProjectAsync(world.ProjectA));

        var chainBefore = await ReadLatestResultVersionAsync(world.TimelineA);
        var projectStampBefore = await ReadProjectUpdatedAtAsync(world.ProjectA);

        // Late revocation at the workspace tier. The MovieTeamMember rows are deliberately left
        // dangling with a role that still nominally grants Edit, so only the fail-closed workspace
        // fence can deny the caller.
        await RemoveWorkspaceMembershipAsync(world.EditorId, world.WorkspaceId);

        using var deniedA = await SendTransitionEditAsync(world.Editor, world.ProjectA, world.TimelineA, world.ShotsA, baseVersion: 2);
        var deniedABody = await deniedA.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, deniedA.StatusCode);
        Assert.True((int)deniedA.StatusCode is >= 400 and < 500, deniedABody);
        AssertJsonDenial(deniedABody);

        // The stale team grant must not authorize the sibling project either.
        using var deniedB = await SendTransitionEditAsync(world.Editor, world.ProjectB, world.TimelineB, world.ShotsB);
        var deniedBBody = await deniedB.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, deniedB.StatusCode);
        Assert.True((int)deniedB.StatusCode is >= 400 and < 500, deniedBBody);
        AssertJsonDenial(deniedBBody);

        // Denied before touching anything: project A's chain is frozen, project B never materialized.
        Assert.Equal(1, await CountEditsForProjectAsync(world.ProjectA));
        Assert.Equal(chainBefore, await ReadLatestResultVersionAsync(world.TimelineA));
        Assert.Equal(1, await ReadTimelineRevisionNumberAsync(world.TimelineA));
        Assert.Equal(0, await CountRevisionsAsync(world.TimelineA));
        Assert.Equal(projectStampBefore, await ReadProjectUpdatedAtAsync(world.ProjectA));
        Assert.Equal(0, await CountEditsForProjectAsync(world.ProjectB));
        Assert.Equal(0, await CountEditsForTimelineAsync(world.TimelineB));
        Assert.Equal(1, await ReadTimelineRevisionNumberAsync(world.TimelineB));

        // No side effect on the unrelated workspace either.
        Assert.Equal(0, await CountEditsForProjectAsync(world.ProjectC));
        Assert.Equal(1, await ReadTimelineRevisionNumberAsync(world.TimelineC));
    }

    [Fact]
    public async Task ProjectScopedEditRevocationDeniesOnlyThatProjectAndLeavesSiblingEditable()
    {
        var world = await CreateWorldAsync("late-rev-project-scope");

        // Formerly an editor on BOTH projects: prove the authority exists before taking it away on
        // one of them, so the denial below cannot be an artifact of a fixture that never granted it.
        using var baseline = await SendTransitionEditAsync(world.Editor, world.ProjectA, world.TimelineA, world.ShotsA);
        var baselineBody = await baseline.Content.ReadAsStringAsync();
        Assert.True(baseline.StatusCode is HttpStatusCode.OK, baselineBody);
        Assert.Equal(2, ReadResultVersion(baselineBody));
        Assert.Equal(1, await CountEditsForProjectAsync(world.ProjectA));

        var chainBefore = await ReadLatestResultVersionAsync(world.TimelineA);

        // Late revocation scoped to one project: an explicit Edit deny override on project A while
        // the same user keeps Edit on project B of the same workspace.
        await SetEditOverrideAsync(world.EditorId, world.ProjectA, granted: false);
        var projectAStampBefore = await ReadProjectUpdatedAtAsync(world.ProjectA);

        using var denied = await SendTransitionEditAsync(world.Editor, world.ProjectA, world.TimelineA, world.ShotsA, baseVersion: 2);
        var deniedBody = await denied.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True((int)denied.StatusCode is >= 400 and < 500, deniedBody);
        AssertJsonDenial(deniedBody);

        // The denied project is untouched beyond the earlier authorized edit: the chain is frozen.
        Assert.Equal(1, await CountEditsForProjectAsync(world.ProjectA));
        Assert.Equal(1, await CountEditsForTimelineAsync(world.TimelineA));
        Assert.Equal(chainBefore, await ReadLatestResultVersionAsync(world.TimelineA));
        Assert.Equal(1, await ReadTimelineRevisionNumberAsync(world.TimelineA));
        Assert.Equal(0, await CountRevisionsAsync(world.TimelineA));
        Assert.Equal(projectAStampBefore, await ReadProjectUpdatedAtAsync(world.ProjectA));

        // The sibling project stays editable: the revocation did not over-leak.
        using var allowed = await SendTransitionEditAsync(world.Editor, world.ProjectB, world.TimelineB, world.ShotsB);
        var allowedBody = await allowed.Content.ReadAsStringAsync();
        Assert.True(allowed.StatusCode is HttpStatusCode.OK, allowedBody);
        Assert.Equal(2, ReadResultVersion(allowedBody));
        Assert.Equal(1, await CountEditsForProjectAsync(world.ProjectB));
        Assert.Equal(2, await ReadLatestResultVersionAsync(world.TimelineB));
        Assert.Equal(1, await ReadTimelineRevisionNumberAsync(world.TimelineB));

        // Neither the denial nor the sibling success crossed a project boundary.
        Assert.Equal(1, await CountEditsForProjectAsync(world.ProjectA));
        Assert.Equal(1, await CountEditsForTimelineAsync(world.TimelineA));
        Assert.Equal(projectAStampBefore, await ReadProjectUpdatedAtAsync(world.ProjectA));
        Assert.Equal(0, await CountEditsForProjectAsync(world.ProjectC));
        Assert.Equal(1, await ReadTimelineRevisionNumberAsync(world.TimelineC));
    }

    private sealed record ProjectSeed(Guid ProjectId, Guid TimelineId, IReadOnlyList<Guid> ShotIds);

    private sealed record World(
        HttpClient Owner,
        HttpClient Editor,
        HttpClient Bystander,
        Guid EditorId,
        Guid WorkspaceId,
        Guid ProjectA,
        Guid TimelineA,
        IReadOnlyList<Guid> ShotsA,
        Guid ProjectB,
        Guid TimelineB,
        IReadOnlyList<Guid> ShotsB,
        Guid ProjectC,
        Guid TimelineC);

    private async Task<World> CreateWorldAsync(string prefix)
    {
        var owner = CreateClient();
        var ownerAuth = await RegisterAsync(owner, $"{prefix} owner");

        var editor = CreateClient();
        var editorAuth = await RegisterAsync(editor, $"{prefix} editor");

        var bystander = CreateClient();
        var bystanderAuth = await RegisterAsync(bystander, $"{prefix} bystander");

        var workspaceId = ownerAuth.PersonalWorkspace.Id;

        ProjectSeed seedA;
        ProjectSeed seedB;
        ProjectSeed seedC;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

            seedA = await SeedProjectAsync(db, workspaceId, ownerAuth.User.Id, $"{prefix} project a");
            seedB = await SeedProjectAsync(db, workspaceId, ownerAuth.User.Id, $"{prefix} project b");
            seedC = await SeedProjectAsync(db, bystanderAuth.PersonalWorkspace.Id, bystanderAuth.User.Id, $"{prefix} project c");

            // The editor joins the owner's workspace and both of the owner's movie teams. Writer
            // grants View, Comment and Edit by role default and no override rows are created, so
            // revoking the authority is the only thing that can deny these requests.
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = editorAuth.User.Id,
                Role = WorkspaceRole.Member, JoinedAt = DateTime.UtcNow,
            });

            foreach (var projectId in new[] { seedA.ProjectId, seedB.ProjectId })
            {
                db.MovieTeamMembers.Add(new MovieTeamMember
                {
                    Id = Guid.NewGuid(), MovieProjectId = projectId, UserId = editorAuth.User.Id,
                    Role = MovieTeamRoles.Writer, IsProjectOwner = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync();
        }

        return new World(
            owner, editor, bystander,
            editorAuth.User.Id, workspaceId,
            seedA.ProjectId, seedA.TimelineId, seedA.ShotIds,
            seedB.ProjectId, seedB.TimelineId, seedB.ShotIds,
            seedC.ProjectId, seedC.TimelineId);
    }

    private async Task<ProjectSeed> SeedProjectAsync(TaslimDbContext db, Guid projectWorkspaceId, Guid creatorId, string title)
    {
        var now = DateTime.UtcNow;
        var projectId = Guid.NewGuid();
        var timelineId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        db.MovieProjects.Add(new MovieProject
        {
            Id = projectId,
            WorkspaceId = projectWorkspaceId,
            CreatedByUserId = creatorId,
            Mode = MovieProjectModes.Full,
            Title = title,
            Description = "Provider-free late-revocation timeline fixture.",
            DurationSeconds = 120,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MovieScenes.Add(new MovieScene
        {
            Id = sceneId, MovieProjectId = projectId, Sequence = 1,
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
            Id = timelineId, MovieProjectId = projectId, CurrentRevisionNumber = 1,
            CreatedAt = now, UpdatedAt = now,
        });
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(), MovieProjectId = projectId, UserId = creatorId,
            Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now,
        });

        await Task.CompletedTask;
        return new ProjectSeed(projectId, timelineId, shotIds);
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
            client,
            HttpMethod.Post,
            $"/api/movie-studio/projects/{movieProjectId}/timeline/transition-edits",
            request);
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
                    Reason: "Synthetic transition edit through the late-revocation boundary test.")),
        };
    }

    /// <summary>
    /// Asserts the denial is the sanitized project-scoped envelope: the established error code and
    /// the generic guard message, serialized as a JSON error body with no exception type, stack
    /// frame, timeline identifier or project title leaking through.
    /// </summary>
    private static JsonElement AssertJsonDenial(string body)
    {
        var json = JsonDocument.Parse(body).RootElement;
        var error = json.GetProperty("error");
        Assert.Equal("MOVIE_PERMISSION_DENIED", error.GetProperty("code").GetString());
        Assert.Equal(GenericDenialMessage, error.GetProperty("message").GetString());
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stack", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at Taslim.", body, StringComparison.Ordinal);
        return error;
    }

    private async Task RemoveTeamMembershipAsync(Guid userId, Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var member = await db.MovieTeamMembers
            .Include(item => item.PermissionOverrides)
            .SingleAsync(item => item.MovieProjectId == movieProjectId && item.UserId == userId);
        db.MovieTeamMemberPermissions.RemoveRange(member.PermissionOverrides);
        db.MovieTeamMembers.Remove(member);
        await db.SaveChangesAsync();
    }

    private async Task RemoveWorkspaceMembershipAsync(Guid userId, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var memberships = await db.WorkspaceMembers
            .Where(item => item.UserId == userId && item.WorkspaceId == workspaceId)
            .ToListAsync();
        db.WorkspaceMembers.RemoveRange(memberships);
        await db.SaveChangesAsync();
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

    private static int ReadResultVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("resultTimelineVersion").GetInt32();
    }

    private async Task<int> CountEditsForProjectAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineTransitionEdits.AsNoTracking().CountAsync(item => item.MovieProjectId == movieProjectId);
    }

    private async Task<int> CountEditsForTimelineAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineTransitionEdits.AsNoTracking().CountAsync(item => item.MovieTimelineId == timelineId);
    }

    private async Task<int> ReadLatestResultVersionAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineTransitionEdits.AsNoTracking()
            .Where(item => item.MovieTimelineId == timelineId)
            .OrderByDescending(item => item.ResultTimelineVersion)
            .Select(item => (int?)item.ResultTimelineVersion)
            .FirstOrDefaultAsync() ?? 0;
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

    private async Task<int> CountRevisionsAsync(Guid timelineId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieTimelineRevisions.AsNoTracking().CountAsync(item => item.MovieTimelineId == timelineId);
    }

    private async Task<DateTime> ReadProjectUpdatedAtAsync(Guid movieProjectId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.MovieProjects.AsNoTracking()
            .Where(item => item.Id == movieProjectId)
            .Select(item => item.UpdatedAt)
            .SingleAsync();
    }

    private HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        clients.Add(client);
        return client;
    }

    private async Task<AuthResponse> RegisterAsync(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"late-revocation-{Guid.NewGuid():N}@example.com",
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
