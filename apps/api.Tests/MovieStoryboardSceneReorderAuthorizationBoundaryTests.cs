using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Authorization boundary of the movie storyboard scene reorder (independent QA lane for PR #959).
///
/// Exercise surface: <c>MovieV2Service.ReorderAsync(userId, "scene", ...)</c> — the service behind
/// <c>PATCH /api/movie-studio/scenes/{id}/order</c>.
///
/// Scope: PR #959 already proves the data-scope half of the isolation boundary (a legitimate
/// owner's reorder of a detached scene no longer renumbers another project's detached scenes and
/// a same-project reorder keeps contiguous 1..n ordinals). This file covers the half that no
/// existing test in the lane drives: the CALLER boundary. A denied caller — either the owner of
/// another synthetic project, or a workspace member whose <c>Edit</c> permission is explicitly
/// revoked by a <see cref="MovieTeamMemberPermission"/> override — must be rejected through the
/// same <c>EnsureAuthorizedAsync(scene.MovieProjectId, Edit)</c> gate, must leave BOTH projects'
/// ordinals and audit stamps byte-identical (no partial mutation on the error path, including no
/// residue of the negative-ordinal staging phase inside <c>MoveAsync</c>), and must not poison
/// the legitimate owner's subsequent same-project reorder.
///
/// Complements, never duplicates: <c>MovieStoryboardSceneOrderingTests.cs</c> (PR #959 lane,
/// data scope), <c>MovieSceneReorderLongFormTimelineTests.cs</c> (attached-scene timeline math
/// and validation bounds), <c>MovieCollaborationTests.cs</c> (team CRUD, no reorder), and
/// <c>MovieStudioV2Tests.cs</c> (act reorder only). None of them calls the scene reorder with a
/// denied caller; on the pre-fix base these cases additionally catch the cross-project ordinal
/// leak through the owner-contrast step.
///
/// Synthetic and provider-free: in-memory SQLite, two synthetic projects in separate personal
/// workspaces, one synthetic collaborator. No provider, no cost reservation, no generation job,
/// no billing, no network, no user data.
/// </summary>
public sealed class MovieStoryboardSceneReorderAuthorizationBoundaryTests
{
    private record SceneRow(Guid Id, int Sequence, DateTime UpdatedAt);

    [Fact]
    public async Task Owner_of_another_project_cannot_reorder_a_foreign_detached_scene_and_nothing_changes_anywhere()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var alpha = await SeedProjectAsync(db, now, "Alpha");
        var beta = await SeedProjectAsync(db, now, "Beta");

        // Overlapping scene identifiers: every scene here is detached from the act/sequence
        // hierarchy (MovieSequenceId == null), so before the #959 scope fix a caller's sibling
        // query matched scenes of BOTH projects by null identifier alone.
        var alphaFirst = DetachedScene(alpha, 1, "Harbor Dawn", now);
        var alphaSecond = DetachedScene(alpha, 2, "Harbor Dusk", now);
        var betaFirst = DetachedScene(beta, 1, "Bazaar Contact", now);
        var betaSecond = DetachedScene(beta, 2, "Bazaar Pursuit", now);
        db.MovieScenes.AddRange(alphaFirst, alphaSecond, betaFirst, betaSecond);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Beta's owner tries to move ALPHA's scene to position 1. The reorder gate authorizes
        // against the TARGET scene's project, so this is rejected through the same
        // not-found contract the service uses to avoid leaking existence.
        await Assert.ThrowsAsync<MovieV2NotFoundException>(() => service.ReorderAsync(beta.UserId, "scene", alphaSecond.Id, 1, CancellationToken.None));

        // Zero partial mutation on the error path: BOTH projects keep their exact ordinals and
        // their untouched UpdatedAt audit stamps; no negative-ordinal staging residue survives.
        await using (var verify = CreateDb(connection))
        {
            var alphaRows = await ReadScenesAsync(verify, alpha.MovieId);
            Assert.Equal([alphaFirst.Id, alphaSecond.Id], alphaRows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2], alphaRows.Select(row => row.Sequence).ToArray());
            Assert.All(alphaRows, row => Assert.Equal(row.UpdatedAt, now));

            var betaRows = await ReadScenesAsync(verify, beta.MovieId);
            Assert.Equal([betaFirst.Id, betaSecond.Id], betaRows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2], betaRows.Select(row => row.Sequence).ToArray());
            Assert.All(betaRows, row => Assert.Equal(row.UpdatedAt, now));
        }

        // And the rejection really is identity-scoped, not scene-scoped: the same caller can
        // still reorder its OWN strip, which then stays inside Beta.
        await service.ReorderAsync(beta.UserId, "scene", betaSecond.Id, 1, CancellationToken.None);
        await using (var verify = CreateDb(connection))
        {
            var betaRows = await ReadScenesAsync(verify, beta.MovieId);
            Assert.Equal([betaSecond.Id, betaFirst.Id], betaRows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2], betaRows.Select(row => row.Sequence).ToArray());

            var alphaRows = await ReadScenesAsync(verify, alpha.MovieId);
            Assert.Equal([alphaFirst.Id, alphaSecond.Id], alphaRows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2], alphaRows.Select(row => row.Sequence).ToArray());
            Assert.All(alphaRows, row => Assert.Equal(row.UpdatedAt, now));
        }
    }

    [Fact]
    public async Task Edit_denied_collaborator_is_rejected_leaves_every_strip_intact_and_the_owner_reorder_still_scopes_to_one_project()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var alpha = await SeedProjectAsync(db, now, "Alpha");
        var beta = await SeedProjectAsync(db, now, "Beta");
        var alphaFirst = DetachedScene(alpha, 1, "Signal Room", now);
        var alphaSecond = DetachedScene(alpha, 2, "Relay Rooftop", now);
        var betaFirst = DetachedScene(beta, 1, "Depot Standoff", now);
        var betaSecond = DetachedScene(beta, 2, "Depot Escape", now);
        db.MovieScenes.AddRange(alphaFirst, alphaSecond, betaFirst, betaSecond);

        // A collaborator INSIDE Alpha's workspace and team, but with the Edit permission
        // explicitly revoked by override: the role default (Writer) would grant Edit, the
        // override must still deny it on the reorder gate.
        var collaboratorId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = collaboratorId, UserName = $"storyboard-noedit-{Guid.NewGuid():N}".ToLowerInvariant(), NormalizedUserName = "STORYBOARD-NOEDIT", DisplayName = "Denied Collaborator", CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = alpha.WorkspaceId, UserId = collaboratorId, Role = WorkspaceRole.Member });
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(),
            MovieProjectId = alpha.MovieId,
            UserId = collaboratorId,
            Role = MovieTeamRoles.Writer,
            IsProjectOwner = false,
            CreatedAt = now,
            UpdatedAt = now,
            PermissionOverrides = [new MovieTeamMemberPermission { Id = Guid.NewGuid(), Permission = MoviePermissions.Edit, Granted = false }],
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Denied caller inside the project: rejected via the same not-found contract...
        await Assert.ThrowsAsync<MovieV2NotFoundException>(() => service.ReorderAsync(collaboratorId, "scene", alphaSecond.Id, 1, CancellationToken.None));

        await using (var verify = CreateDb(connection))
        {
            foreach (var (projectId, expected) in new[] { (alpha.MovieId, new[] { 1, 2 }), (beta.MovieId, new[] { 1, 2 }) })
            {
                var rows = await ReadScenesAsync(verify, projectId);
                Assert.Equal(expected, rows.Select(row => row.Sequence).ToArray());
                Assert.All(rows, row => Assert.Equal(row.UpdatedAt, now));
            }
        }

        // ...and the rejection leaves no staging residue: the legitimate owner's same-project
        // reorder immediately afterwards still lands as contiguous 1..n inside Alpha only.
        await service.ReorderAsync(alpha.UserId, "scene", alphaSecond.Id, 1, CancellationToken.None);
        await using (var verify = CreateDb(connection))
        {
            var alphaRows = await ReadScenesAsync(verify, alpha.MovieId);
            Assert.Equal([alphaSecond.Id, alphaFirst.Id], alphaRows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2], alphaRows.Select(row => row.Sequence).ToArray());
            Assert.All(alphaRows, row => Assert.True(row.Sequence >= 1));

            var betaRows = await ReadScenesAsync(verify, beta.MovieId);
            Assert.Equal([betaFirst.Id, betaSecond.Id], betaRows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2], betaRows.Select(row => row.Sequence).ToArray());
            Assert.All(betaRows, row => Assert.Equal(row.UpdatedAt, now));
        }
    }

    private sealed record SeededProject(Guid UserId, Guid WorkspaceId, Guid MovieId);

    private static MovieScene DetachedScene(SeededProject project, int sequence, string title, DateTime now) =>
        new() { Id = Guid.NewGuid(), MovieProjectId = project.MovieId, Sequence = sequence, Title = title, Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };

    private static MovieV2Service CreateService(TaslimDbContext db) => new(db, new WorkspaceAccessService(db), new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<SceneRow[]> ReadScenesAsync(TaslimDbContext db, Guid movieProjectId) => await db.MovieScenes.AsNoTracking()
        .Where(item => item.MovieProjectId == movieProjectId)
        .OrderBy(item => item.Sequence)
        .Select(item => new SceneRow(item.Id, item.Sequence, item.UpdatedAt))
        .ToArrayAsync();

    private static async Task<SeededProject> SeedProjectAsync(TaslimDbContext db, DateTime now, string tag)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"storyboard-authz-{tag}-{Guid.NewGuid():N}".ToLowerInvariant(), NormalizedUserName = $"STORYBOARD-AUTHZ-{tag}".ToUpperInvariant(), DisplayName = $"Storyboard Authz {tag}", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = $"Storyboard Authz {tag}", Slug = $"storyboard-authz-{tag}-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = $"Project {tag}", Description = "A synthetic feature.", DurationSeconds = 120, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return new SeededProject(userId, workspaceId, movieId);
    }
}
