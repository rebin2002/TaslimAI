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
/// Movie storyboard scene reorder: late access-revocation and project-archive fence shape.
///
/// Exercise surface: <c>MovieV2Service.ReorderAsync(userId, "scene", ...)</c> — the service
/// behind <c>PATCH /api/movie-studio/scenes/{id}/order</c> — specifically the moment a caller's
/// access is revoked AFTER an earlier reorder from the same caller legitimately succeeded
/// ("late revocation"), and the shape of the fence when the project is ARCHIVED.
///
/// Residual invariant pinned here: <c>ReorderAsync</c> opens a transaction, re-reads the scene
/// row, and calls <c>EnsureAuthorizedAsync</c> — which resolves the Edit grant fresh through
/// <c>MovieCollaborationAccess.GetMemberAsync</c>: first the workspace-membership fence, then
/// the team row. No grant decision is ever cached across requests, so every revocation that
/// commits before a request starts must deny that request with the service's not-found contract
/// AND mutate nothing: no ordinal renumbering, no <c>UpdatedAt</c> rewrite, no leak of
/// <c>MoveAsync</c>'s negative-ordinal staging phase. Two revocation vectors are pinned:
/// (1) workspace-member removal with the <c>MovieTeamMember</c> row left dangling — the stale
/// owner row still nominally grants Edit through <c>IsProjectOwner</c>, so only the
/// fail-closed workspace fence can deny it; (2) team-row removal while workspace membership
/// remains. And the archive shape: archiving a project (<c>SetProjectArchivedAsync</c>) is a
/// production-status transition, not an access revocation — it must NOT change the fence:
/// a live member can still reorder their own archived project (this is the current V2 contract;
/// unlike v1 <c>MovieStudioService.ReorderShotAsync</c>, which rejects archived shots, V2 gates
/// solely on the permission fence), while a revoked caller still gets nothing on an archived
/// project either.
///
/// Determinism note: with the single-connection in-memory SQLite fixture, SQLite serializes
/// writers, so a revocation cannot commit strictly between the authorization read and the
/// commit INSIDE one in-flight ReorderAsync transaction. That check-then-write window inside a
/// single request is a hosted-Postgres concern (covered by the "API integration tests" gate);
/// this lane pins the observable per-request contract with zero timing dependence, in the same
/// shape as the #965 timeline lane's <c>MovieTimelineLateRevocationProjectScopeTests</c> chose
/// for its own endpoint: revoke-then-next-request fails closed and leaves every row untouched.
///
/// Non-duplication evidence (re-checked 2026-11-19 against all open PRs and remote branches):
/// #959 (head 420cb187) owns the DATA-scope half — <c>MovieStoryboardSceneOrderingTests</c> and
/// the project-ID predicate; this file adds neither and edits no shared source. #1114 owns the
/// STATIC denied-caller half — outsider and override-denied Writer on scenes that were never
/// theirs; nothing there performs a successful reorder first. #1060 pins the same static denial
/// set ("denies unauthorized actors and mutates no scene rows") with a positive control; it too
/// never revokes an actor whose earlier reorder landed. #1099 pins late revocation — but only
/// for timeline transition-edits (#965, <c>MovieStudioController</c> clip path), not for
/// <c>ReorderAsync("scene")</c>. No open PR or branch adds a file named
/// <c>MovieStoryboardSceneReorderLateRevocation*</c>. Every strip here is attached to a real
/// sequence, so nothing depends on the detached-scene leak that #959 fixes: these tests pass
/// identically with and without the #959 predicate.
///
/// Synthetic and provider-free: in-memory SQLite, one connection per test, fresh DbContexts
/// across phases so reads see committed rows only, synthetic fixtures, no provider, no cost
/// reservation, no generation job, no billing, no network, no user data, no migrations.
/// </summary>
public sealed class MovieStoryboardSceneReorderLateRevocationAndArchiveTests
{
    private record SceneRow(Guid Id, int Sequence, string Title, DateTime UpdatedAt);
    private sealed record SeededProject(Guid UserId, Guid WorkspaceId, Guid MovieId);

    [Fact]
    public async Task Workspace_revocation_after_a_successful_scene_reorder_denies_the_next_reorder_despite_the_dangling_owner_team_row()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var alpha = await SeedProjectAsync(db, now, "Alpha");
        var beta = await SeedProjectAsync(db, now, "Beta");
        var service = CreateService(db);
        var alphaScenes = await BuildSequenceStripAsync(service, alpha, "Opening", ["Scene One", "Scene Two", "Scene Three", "Scene Four"]);
        var betaScenes = await BuildSequenceStripAsync(service, beta, "Bazaar", ["Bazaar Contact", "Bazaar Pursuit"]);

        // Phase 1: the owner's reorder legitimately lands — proof the fixture really mutates.
        await service.ReorderAsync(alpha.UserId, "scene", alphaScenes[3], 1, CancellationToken.None);
        SceneRow[] alphaAfterLegitimateReorder;
        SceneRow[] betaBaseline;
        await using (var snapshot = CreateDb(connection))
        {
            alphaAfterLegitimateReorder = await ReadStripAsync(snapshot, alpha.MovieId);
            betaBaseline = await ReadStripAsync(snapshot, beta.MovieId);
            Assert.Equal([alphaScenes[3], alphaScenes[0], alphaScenes[1], alphaScenes[2]], alphaAfterLegitimateReorder.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2, 3, 4], alphaAfterLegitimateReorder.Select(row => row.Sequence).ToArray());
        }

        // Late revocation: the workspace member row is deleted while the IsProjectOwner team
        // row is deliberately left dangling — it still nominally grants Edit.
        await using (var revoke = CreateDb(connection))
        {
            var membership = await revoke.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == alpha.WorkspaceId && item.UserId == alpha.UserId);
            revoke.WorkspaceMembers.Remove(membership);
            await revoke.SaveChangesAsync();
        }

        // The very next reorder from the same principal must fail closed with the not-found
        // contract, through a fresh context (a fresh request), not a stale grant.
        await using (var denied = CreateDb(connection))
        {
            await Assert.ThrowsAsync<MovieV2NotFoundException>(() => CreateService(denied).ReorderAsync(alpha.UserId, "scene", alphaScenes[0], 3, CancellationToken.None));
        }

        // Zero-mutation proof and no staging residue, read through a fresh context.
        await using (var verify = CreateDb(connection))
        {
            Assert.Equal(alphaAfterLegitimateReorder, await ReadStripAsync(verify, alpha.MovieId));
            Assert.Equal(betaBaseline, await ReadStripAsync(verify, beta.MovieId));
            Assert.False(await verify.MovieScenes.AnyAsync(item => item.Sequence < 1), "MoveAsync's negative-ordinal staging must never be visible in committed rows.");
        }

        // Identity-scoped contrast: a live-member owner of a DIFFERENT workspace still reorders
        // successfully — the denial above is a fence decision, not fixture-wide breakage — and
        // Alpha's frozen strip still moves under nobody's hand.
        await using (var contrast = CreateDb(connection))
        {
            await CreateService(contrast).ReorderAsync(beta.UserId, "scene", betaScenes[1], 1, CancellationToken.None);
        }
        await using (var final = CreateDb(connection))
        {
            Assert.Equal([betaScenes[1], betaScenes[0]], (await ReadStripAsync(final, beta.MovieId)).Select(row => row.Id).ToArray());
            Assert.Equal(alphaAfterLegitimateReorder, await ReadStripAsync(final, alpha.MovieId));
        }
    }

    [Fact]
    public async Task Team_row_removal_denies_the_next_scene_reorder_without_touching_a_single_row_and_rejoining_restores_the_grant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var gamma = await SeedProjectAsync(db, now, "Gamma");
        var service = CreateService(db);
        var scenes = await BuildSequenceStripAsync(service, gamma, "Chase", ["Dawn", "Rooftop", "Dusk"]);

        // Phase 1: legitimate reorder lands (Dusk jumps to the front).
        await service.ReorderAsync(gamma.UserId, "scene", scenes[2], 1, CancellationToken.None);
        SceneRow[] afterLegitimate;
        await using (var snapshot = CreateDb(connection))
        {
            afterLegitimate = await ReadStripAsync(snapshot, gamma.MovieId);
            Assert.Equal([scenes[2], scenes[0], scenes[1]], afterLegitimate.Select(row => row.Id).ToArray());
        }

        // Late revocation vector 2: workspace membership stays; only the team row is removed.
        await using (var revoke = CreateDb(connection))
        {
            var teamRow = await revoke.MovieTeamMembers.SingleAsync(item => item.MovieProjectId == gamma.MovieId && item.UserId == gamma.UserId);
            revoke.MovieTeamMembers.Remove(teamRow);
            await revoke.SaveChangesAsync();
        }

        await using (var denied = CreateDb(connection))
        {
            await Assert.ThrowsAsync<MovieV2NotFoundException>(() => CreateService(denied).ReorderAsync(gamma.UserId, "scene", scenes[0], 2, CancellationToken.None));
        }
        await using (var verify = CreateDb(connection))
        {
            Assert.Equal(afterLegitimate, await ReadStripAsync(verify, gamma.MovieId));
            Assert.False(await verify.MovieScenes.AnyAsync(item => item.Sequence < 1));
        }

        // Self-contained positive control: rejoining the team restores the grant and the very
        // same call lands — the rejection above was the revocation, not fixture decay.
        await using (var rejoin = CreateDb(connection))
        {
            rejoin.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = gamma.MovieId, UserId = gamma.UserId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
            await rejoin.SaveChangesAsync();
        }
        await using (var restored = CreateDb(connection))
        {
            await CreateService(restored).ReorderAsync(gamma.UserId, "scene", scenes[0], 3, CancellationToken.None);
        }
        await using (var final = CreateDb(connection))
        {
            var rows = await ReadStripAsync(final, gamma.MovieId);
            Assert.NotEqual(afterLegitimate, rows);
            Assert.Equal([1, 2, 3], rows.Select(row => row.Sequence).ToArray());
        }
    }

    [Fact]
    public async Task Project_archive_is_not_an_access_revocation_for_a_live_member_yet_a_revoked_caller_still_changes_nothing_on_an_archived_project()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var delta = await SeedProjectAsync(db, now, "Delta");
        var service = CreateService(db);
        var scenes = await BuildSequenceStripAsync(service, delta, "Finale", ["Curtain", "Bows", "Exit"]);

        // Archive the project through the real endpoint path.
        var archived = await service.SetProjectArchivedAsync(delta.UserId, delta.MovieId, true, CancellationToken.None);
        Assert.NotNull(archived);
        Assert.Equal(MovieProductionStatuses.Archived, archived!.ProductionStatus);
        Assert.NotNull(archived.ArchivedAt);

        // Contract pinned by this lane (documented current behavior, observed as an asymmetry
        // with v1 MovieStudioService, which refuses to reorder archived shots): V2's hierarchy
        // fence is purely the membership+permission check — archiving is a status transition a
        // member can perform on their own project and does not strip their own write access.
        // Therefore a live member's scene reorder still lands on an archived project.
        await service.ReorderAsync(delta.UserId, "scene", scenes[2], 1, CancellationToken.None);
        SceneRow[] afterArchiveReorder;
        await using (var snapshot = CreateDb(connection))
        {
            afterArchiveReorder = await ReadStripAsync(snapshot, delta.MovieId);
            Assert.Equal([scenes[2], scenes[0], scenes[1]], afterArchiveReorder.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2, 3], afterArchiveReorder.Select(row => row.Sequence).ToArray());
        }

        // The combined answer to the race question: with the project archived AND the caller's
        // workspace membership revoked, the reorder still cannot mutate the strip.
        await using (var revoke = CreateDb(connection))
        {
            var membership = await revoke.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == delta.WorkspaceId && item.UserId == delta.UserId);
            revoke.WorkspaceMembers.Remove(membership);
            await revoke.SaveChangesAsync();
        }
        await using (var denied = CreateDb(connection))
        {
            await Assert.ThrowsAsync<MovieV2NotFoundException>(() => CreateService(denied).ReorderAsync(delta.UserId, "scene", scenes[0], 2, CancellationToken.None));
        }
        await using (var final = CreateDb(connection))
        {
            Assert.Equal(afterArchiveReorder, await ReadStripAsync(final, delta.MovieId));
            Assert.False(await final.MovieScenes.AnyAsync(item => item.Sequence < 1), "No negative-ordinal staging residue may persist after a denied reorder.");
        }
    }

    private static async Task<Guid[]> BuildSequenceStripAsync(MovieV2Service service, SeededProject project, string tag, string[] titles)
    {
        var act = await service.AddActAsync(project.UserId, project.MovieId, new MovieV2ActRequest { Title = $"Act {tag}" }, CancellationToken.None);
        var sequence = await service.AddSequenceAsync(project.UserId, act!.Id, new MovieV2SequenceRequest { Title = $"Strip {tag}" }, CancellationToken.None);
        var ids = new List<Guid>();
        foreach (var title in titles)
        {
            var scene = await service.AddSceneAsync(project.UserId, sequence!.Id, new MovieV2SceneRequest { Title = title, Summary = "Synthetic scene." }, CancellationToken.None);
            ids.Add(scene!.Id);
        }
        return [.. ids];
    }

    private static async Task<SceneRow[]> ReadStripAsync(TaslimDbContext db, Guid movieProjectId) => await db.MovieScenes.AsNoTracking()
        .Where(item => item.MovieProjectId == movieProjectId)
        .OrderBy(item => item.Sequence)
        .Select(item => new SceneRow(item.Id, item.Sequence, item.Title, item.UpdatedAt))
        .ToArrayAsync();

    private static MovieV2Service CreateService(TaslimDbContext db) => new(db, new WorkspaceAccessService(db), new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<SeededProject> SeedProjectAsync(TaslimDbContext db, DateTime now, string tag)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"late-rev-{tag}-{Guid.NewGuid():N}".ToLowerInvariant(), NormalizedUserName = $"LATE-REV-{tag}".ToUpperInvariant(), DisplayName = $"Late Revocation {tag}", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = $"Late Revocation {tag}", Slug = $"late-rev-{tag}-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = $"Late-Rev Project {tag}", Description = "A synthetic feature.", DurationSeconds = 120, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return new SeededProject(userId, workspaceId, movieId);
    }
}
