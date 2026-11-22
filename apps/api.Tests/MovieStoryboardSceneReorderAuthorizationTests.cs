using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Movie storyboard scene reorder: authorization rejections must leave every row untouched.
///
/// Exercise surface: <c>MovieV2Service.ReorderAsync(userId, "scene", ...)</c> — the same service
/// call lane under review in PR #959 (the project-scoped sibling-set predicate, line
/// <c>Where(item => item.MovieProjectId == scene!.MovieProjectId && ...)</c>). PR #959's
/// <c>MovieStoryboardSceneOrderingTests</c> pins what an AUTHORIZED reorder must and must not
/// renumber, and the whole suite asserts <c>MovieV2NotFoundException</c> nowhere for any
/// <c>ReorderAsync</c> entity: the rejection branch itself — <c>EnsureAuthorizedAsync(scene?.MovieProject.WorkspaceId, userId, ..., scene?.MovieProjectId, MoviePermissions.Edit)</c>
/// -> <c>MovieCollaborationAccess.HasPermissionAsync</c> — was untested.
///
/// This lane pins the three authorization branches the denial path actually walks, in
/// increasing privilege order, and proves each rejection is zero-mutation:
/// 1. a foreign tenant — the owner of a DIFFERENT project/workspace — targeting another
///    project's scene id directly (cross-tenant reorder attempt);
/// 2. a bare workspace member (a <c>WorkspaceMember</c> row but NO <c>MovieTeamMember</c> row),
///    which <c>GetMemberAsync</c> must reject via the null-team-member branch;
/// 3. a project team member whose role default DOES include Edit (Writer) but who carries an
///    explicit <c>MovieTeamMemberPermission { Permission = Edit, Granted = false }</c> override,
///    the <c>overridePermission?.Granted ?? ...</c> branch.
/// A positive control at the end — the real owner successfully reordering an authorized,
/// sequence-attached strip inside the SAME fixture — proves the denials above are
/// authorization decisions and not fixture breakage (the assertion is not vacuous).
///
/// Fix-independent by construction: the rejection throws before the sibling-set query runs, so
/// this file is deterministic RED-on-breakage/GREEN-today on current main and on PR #959's
/// head, whichever lands first; it never depends on the #959 predicate to pass.
///
/// Non-duplication evidence (2026-11-18): no open PR adds a file matching
/// <c>*SceneReorder*Authorization*</c>; #959 owns <c>MovieStoryboardSceneOrderingTests.cs</c>
/// (ordering/isolation only, zero denial assertions); #965 (OPEN) maps timeline CLIP edit
/// authorization at <c>MovieStudioController</c> (403-vs-500 envelope), a different method and
/// transport; #870 maps caption-cue delete authorization. <c>grep -rln "MovieV2NotFoundException"
/// apps/api.Tests</c> returned no files on head 64afc377 before this file was added.
///
/// Synthetic and provider-free: in-memory SQLite, fabricated users, workspaces and projects.
/// No <c>IMovieVideoProvider</c>, no cost reservation, no generation job, no billing, no
/// network, no real user data.
/// </summary>
public sealed class MovieStoryboardSceneReorderAuthorizationTests
{
    private record SceneSnapshot(Guid Id, Guid MovieProjectId, Guid? MovieSequenceId, int Sequence, string Title, DateTime UpdatedAt);

    [Fact]
    public async Task ReorderAsync_scene_denies_unauthorized_actors_and_mutates_no_scene_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;

        var owner = await SeedProjectAsync(db, now, "own");
        var foreignTenant = await SeedProjectAsync(db, now, "for");
        // Branch 2 actor: member of the OWNER'S workspace, but with no MovieTeamMember row.
        var bareMemberId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = bareMemberId, UserName = $"reorder-auth-bare-{Guid.NewGuid():N}".ToLowerInvariant(), NormalizedUserName = $"REORDER-AUTH-BARE-{Guid.NewGuid():N}".ToUpperInvariant(), DisplayName = "Bare Workspace Member", CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = owner.WorkspaceId, UserId = bareMemberId, Role = WorkspaceRole.Member });
        // Branch 3 actor: Writer team member (role default includes Edit) with an explicit Edit-deny override.
        var writerDeniedId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = writerDeniedId, UserName = $"reorder-auth-writer-{Guid.NewGuid():N}".ToLowerInvariant(), NormalizedUserName = $"REORDER-AUTH-WRITER-{Guid.NewGuid():N}".ToUpperInvariant(), DisplayName = "Writer Edit-Denied", CreatedAt = now, UpdatedAt = now });
        var writerMemberRow = new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = owner.MovieId, UserId = writerDeniedId, Role = MovieTeamRoles.Writer, IsProjectOwner = false, CreatedAt = now, UpdatedAt = now };
        db.MovieTeamMembers.Add(writerMemberRow);
        db.MovieTeamMemberPermissions.Add(new MovieTeamMemberPermission { Id = Guid.NewGuid(), MovieTeamMemberId = writerMemberRow.Id, Permission = MoviePermissions.Edit, Granted = false });

        // Two detached (legacy-shape, MovieSequenceId == null) scenes in each project so any
        // leaked write would land on rows the attackers do not own under every predicate shape.
        var targetFirst = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = owner.MovieId, Sequence = 1, Title = "Owner Scene One", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        var targetSecond = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = owner.MovieId, Sequence = 2, Title = "Owner Scene Two", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        var foreignFirst = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = foreignTenant.MovieId, Sequence = 1, Title = "Foreign Scene One", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        var foreignSecond = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = foreignTenant.MovieId, Sequence = 2, Title = "Foreign Scene Two", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        db.MovieScenes.AddRange(targetFirst, targetSecond, foreignFirst, foreignSecond);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // All three denials: same victim scene, escalating attacker privilege, always the
        // closed-envelope MovieV2NotFoundException (no existence oracle, no 500).
        await Assert.ThrowsAsync<MovieV2NotFoundException>(() => service.ReorderAsync(foreignTenant.UserId, "scene", targetFirst.Id, 2, CancellationToken.None));
        await Assert.ThrowsAsync<MovieV2NotFoundException>(() => service.ReorderAsync(bareMemberId, "scene", targetFirst.Id, 2, CancellationToken.None));
        await Assert.ThrowsAsync<MovieV2NotFoundException>(() => service.ReorderAsync(writerDeniedId, "scene", targetFirst.Id, 2, CancellationToken.None));

        // Zero-mutation proof, read through a fresh context: every scene row in the database
        // still carries its seeded ordinal, parent linkage and audit stamp.
        await using (var after = CreateDb(connection))
        {
            Assert.Equal(ExpectedDetachedRows(), await ReadSnapshotsAsync(after));
        }

        // Positive control: the real owner reorders an authorized sequence-attached strip in
        // this very fixture. If the denials above were fixture breakage, this would fail too.
        var act = await service.AddActAsync(owner.UserId, owner.MovieId, new MovieV2ActRequest { Title = "Control Act" }, CancellationToken.None);
        var sequence = await service.AddSequenceAsync(owner.UserId, act!.Id, new MovieV2SequenceRequest { Title = "Control Strip" }, CancellationToken.None);
        var controlFirst = await service.AddSceneAsync(owner.UserId, sequence!.Id, new MovieV2SceneRequest { Title = "Control One", Summary = "Synthetic scene." }, CancellationToken.None);
        var controlSecond = await service.AddSceneAsync(owner.UserId, sequence.Id, new MovieV2SceneRequest { Title = "Control Two", Summary = "Synthetic scene." }, CancellationToken.None);
        await service.ReorderAsync(owner.UserId, "scene", controlSecond!.Id, 1, CancellationToken.None);

        await using var final = CreateDb(connection);
        var controlRows = await final.MovieScenes.AsNoTracking()
            .Where(item => item.MovieSequenceId == sequence.Id)
            .OrderBy(item => item.Sequence)
            .Select(item => item.Id)
            .ToArrayAsync();
        Assert.Equal([controlSecond.Id, controlFirst!.Id], controlRows);
        // The detached victim rows are still untouched after the control's real write ran,
        // i.e. the only rows any of these calls ever renumbered were the authorized ones.
        Assert.Equal(ExpectedDetachedRows(), await ReadSnapshotsAsync(final));

        // The four detached rows exactly as seeded, ordered by Title to match ReadSnapshotsAsync.
        SceneSnapshot[] ExpectedDetachedRows() =>
        [
            new SceneSnapshot(foreignFirst.Id, foreignTenant.MovieId, null, 1, "Foreign Scene One", now),
            new SceneSnapshot(foreignSecond.Id, foreignTenant.MovieId, null, 2, "Foreign Scene Two", now),
            new SceneSnapshot(targetFirst.Id, owner.MovieId, null, 1, "Owner Scene One", now),
            new SceneSnapshot(targetSecond.Id, owner.MovieId, null, 2, "Owner Scene Two", now),
        ];
    }

    private static async Task<SceneSnapshot[]> ReadSnapshotsAsync(TaslimDbContext db) => await db.MovieScenes.AsNoTracking()
        .Where(item => item.MovieSequenceId == null)
        .OrderBy(item => item.Title)
        .Select(item => new SceneSnapshot(item.Id, item.MovieProjectId, item.MovieSequenceId, item.Sequence, item.Title, item.UpdatedAt))
        .ToArrayAsync();

    private sealed record SeededProject(Guid UserId, Guid WorkspaceId, Guid MovieId);

    private static MovieV2Service CreateService(TaslimDbContext db) => new(db, new WorkspaceAccessService(db), new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<SeededProject> SeedProjectAsync(TaslimDbContext db, DateTime now, string tag)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"reorder-auth-{tag}-{Guid.NewGuid():N}".ToLowerInvariant(), NormalizedUserName = $"REORDER-AUTH-{tag}-{Guid.NewGuid():N}".ToUpperInvariant(), DisplayName = $"Reorder Auth {tag}", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = $"Reorder Auth {tag}", Slug = $"reorder-auth-{tag}-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = $"Reorder Auth Project {tag}", Description = "A synthetic feature.", DurationSeconds = 120, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return new SeededProject(userId, workspaceId, movieId);
    }
}
