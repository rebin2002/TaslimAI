using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;
using ProjectRevision = Taslim.Api.Projects.ProjectRevision;
using RevisionFence = Taslim.Api.Projects.RevisionFence;
using RevisionWriteOutcome = Taslim.Api.Projects.RevisionWriteOutcome;
using RevisionWriteResult = Taslim.Api.Projects.ProjectRevisionWriteResult;

namespace Taslim.Api.Tests;

/// <summary>
/// The revision stamp is only useful as an ETag if it never goes backwards and is never handed
/// out twice. <c>NextStamp</c> guarantees that against the base it is given, which is why the
/// guarded (<c>If-Match</c> with a concrete token) path was already safe: its base is the row.
/// The unconditional paths were not. A wildcard write, a legacy write with no header, an archive
/// and a restore all derived their stamp from a value read <em>before</em> the statement ran, so a
/// commit landing in between could be overwritten by an older — or identical — stamp, making an
/// already-expired ETag current again, or answer a legacy client with a conflict it cannot act on.
///
/// Those interleavings cannot be scheduled over HTTP (the window is two statements wide inside one
/// request), so the ordering is declared here instead: the row is demonstrably advanced past the
/// revision a writer observed, and the write is then handed that stale revision. That is the exact
/// state a mid-request commit produces, reached without thread timing, and every case below fails
/// on the pre-fix shape — the stamped write either lands behind the committed stamp (wildcard,
/// archive, restore) or refuses a request that promised last-write-wins (legacy).
/// </summary>
public sealed class ProjectRevisionMonotonicityTests : IClassFixture<TaslimApiFactory>
{
    private const string Password = "StrongPassword!234";
    private readonly TaslimApiFactory factory;

    public ProjectRevisionMonotonicityTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // -----------------------------------------------------------------------------------------------
    // The stamped-write primitive: what a mid-request commit does to each conditional form.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task StampedWrite_LiveRowFence_WithACommitLandingAfterTheRead_LandsAheadOfTheCommittedRevision()
    {
        var projectId = await SeedProjectAsync("Forced writer");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        var observed = await ReadRevisionAsync(db, projectId);
        var committed = await CommitAheadOfAsync(db, projectId, observed, "Committed by the intervening writer");

        var result = await WriteNameAsync(db, projectId, observed, RevisionFence.LiveRow, "Forced rename");

        // Pre-fix, the wildcard leg was an unconditional UPDATE that published NextStamp(observed, …)
        // — a stamp taken from the read above, i.e. behind `committed`. The row would then carry a
        // revision that had already been handed out and expired, so the intervening writer's ETag
        // became valid again.
        Assert.Equal(RevisionWriteOutcome.Applied, result.Outcome);
        Assert.True(result.Revision > committed,
            $"the accepted write must land ahead of the revision that was committed mid-request (committed {committed:O}, published {result.Revision:O})");

        var row = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId);
        Assert.Equal(result.Revision, row.UpdatedAt);
        Assert.Equal("Forced rename", row.Name);
        Assert.Equal("Committed by the intervening writer", row.Description);
    }

    [Fact]
    public async Task StampedWrite_DeclaredFence_WithACommitLandingAfterTheRead_ConflictsWithoutTouchingTheRow()
    {
        var projectId = await SeedProjectAsync("Declared writer");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        var observed = await ReadRevisionAsync(db, projectId);
        var committed = await CommitAheadOfAsync(db, projectId, observed, "Committed by the intervening writer");

        var result = await WriteNameAsync(db, projectId, observed, RevisionFence.Declared, "Must not land");

        // The client's base is what it declared; re-targeting it would merge an edit into content the
        // client never saw. This is the guarantee #929 and #1174 pin, and the retry loop must not
        // widen it into a silent apply.
        Assert.Equal(RevisionWriteOutcome.Conflict, result.Outcome);
        Assert.Equal(committed, result.Revision);

        var row = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId);
        Assert.Equal("Declared writer", row.Name);
        Assert.Equal("Committed by the intervening writer", row.Description);
        Assert.Equal(committed, row.UpdatedAt);
    }

    [Fact]
    public async Task StampedWrite_WritersThatAllReadTheSameBase_LandOneAfterAnother_WithDistinctRevisions()
    {
        const int writers = 12;
        var projectId = await SeedProjectAsync("Shared base");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        // Every writer read the same revision — the worst-case pile-up, as if a commit landed after
        // each read. Each must still publish a stamp no writer has seen before, in the order the
        // writes actually landed.
        var sharedBase = await ReadRevisionAsync(db, projectId);
        var published = new List<DateTime>();
        for (var writer = 0; writer < writers; writer++)
        {
            var revision = await CommitAheadOfAsync(db, projectId, sharedBase, $"Intervening commit {writer}");
            var result = await WriteNameAsync(db, projectId, sharedBase, RevisionFence.LiveRow, $"Writer {writer}");
            Assert.Equal(RevisionWriteOutcome.Applied, result.Outcome);
            Assert.True(result.Revision > revision, "each accepted write must outrank the commit that beat it to the row");
            published.Add(result.Revision);
            sharedBase = revision;
        }

        Assert.Equal(writers, published.Distinct().Count());
        Assert.Equal(published.OrderBy(item => item), published);
    }

    [Fact]
    public async Task StampedWrite_AgainstADeletedRow_ReportsMissingRatherThanAConflict()
    {
        var projectId = await SeedProjectAsync("Doomed project");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        var observed = await ReadRevisionAsync(db, projectId);
        db.Projects.Remove(await db.Projects.SingleAsync(item => item.Id == projectId));
        await db.SaveChangesAsync();

        var result = await WriteNameAsync(db, projectId, observed, RevisionFence.LiveRow, "Must not land");

        // A row that vanished is not a revision conflict; answering 409 would tell the client the
        // project still exists and invites it to retry against a stamp that will never appear.
        Assert.Equal(RevisionWriteOutcome.Missing, result.Outcome);
    }

    [Fact]
    public async Task StampedWrite_PublishesOnlyStampsTheStoreCanReturnVerbatim()
    {
        var projectId = await SeedProjectAsync("Grid");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        // The sub-microsecond base stands in for a value that arrived with digits the columns drop:
        // every published stamp has to be on the microsecond grid, otherwise the store rounds it and
        // the ETag the response header carries is not the value any later compare-and-set matches.
        var subMicrosecondBase = new DateTime(2026, 11, 24, 9, 30, 0, DateTimeKind.Unspecified).AddTicks(123_456_705);
        await db.Projects.Where(item => item.Id == projectId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, subMicrosecondBase));
        var observed = await ReadRevisionAsync(db, projectId);

        var result = await WriteNameAsync(db, projectId, observed, RevisionFence.LiveRow, "Grid rename");

        Assert.Equal(RevisionWriteOutcome.Applied, result.Outcome);
        Assert.Equal(0, result.Revision.Ticks % 10);
        var token = ProjectRevision.FormatETag(result.Revision);
        Assert.True(ProjectRevision.TryParseETag(token, out var reparsed));
        Assert.Equal(result.Revision.Ticks, reparsed.Ticks);
        Assert.Equal(result.Revision.Ticks, (await ReadRevisionAsync(db, projectId)).Ticks);
    }

    // -----------------------------------------------------------------------------------------------
    // Archive and restore publish the same ETag, so they carry the same obligation.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Archive_WhenTheRowMovedPastItsRead_PublishesAStampAheadOfTheCommittedRevision()
    {
        var (client, projectId) = await CreateProjectWithClientAsync();
        var observed = await ReadRevisionAsync(client, projectId);

        // The state a commit landing after the endpoint's read produces, reached without thread
        // timing: the row now carries a revision the archive cannot assume its own clock beats.
        var committed = observed.AddSeconds(60);
        await AdvanceRowExternallyAsync(projectId, committed, "Committed by the intervening writer");

        using var response = await PostAsync(client, $"/api/projects/{projectId}/archive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var archived = await response.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(archived);
        Assert.Equal(ProjectStatuses.Archived, archived.Status);
        Assert.NotNull(archived.ArchivedAt);
        Assert.Equal("Committed by the intervening writer", archived.Description);

        // Pre-fix this was a bare `project.UpdatedAt = DateTime.UtcNow` on the tracked entity: the
        // stamp moved backwards, the intervening writer's ETag became current again, and the
        // response advertised a revision the row did not hold.
        Assert.True(archived.UpdatedAt > committed,
            $"archive must not move the revision backwards (committed {committed:O}, published {archived.UpdatedAt:O})");

        // Archive answers with the stamp in its body rather than an ETag header, so the header the
        // client would actually use for its next edit is the one a read publishes: it has to agree.
        using (var read = await client.GetAsync($"/api/projects/{projectId}"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.NotNull(read.Headers.ETag);
            Assert.Equal(archived.UpdatedAt.Ticks, ParseTicks(read.Headers.ETag!.ToString()));
        }

        // And the revision it displaced must stay expired.
        var staleEdit = await PatchAsync(client, projectId, FormatIfMatchToken(committed), Form(archived, name: "Must not land"));
        Assert.Equal(HttpStatusCode.Conflict, staleEdit.StatusCode);
    }

    [Fact]
    public async Task Restore_WhenTheRowMovedPastItsRead_PublishesAStampAheadOfTheCommittedRevision()
    {
        var (client, projectId) = await CreateProjectWithClientAsync();
        var observed = await ReadRevisionAsync(client, projectId);
        var committed = observed.AddSeconds(60);
        await AdvanceRowExternallyAsync(projectId, committed, "Committed by the intervening writer");

        using var response = await PostAsync(client, $"/api/projects/{projectId}/restore");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var restored = await response.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(restored);
        Assert.Equal(ProjectStatuses.Active, restored.Status);
        Assert.Null(restored.ArchivedAt);
        Assert.True(restored.UpdatedAt > committed,
            $"restore must not move the revision backwards (committed {committed:O}, published {restored.UpdatedAt:O})");
        Assert.Equal("Committed by the intervening writer", restored.Description);
        using var read = await client.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(restored.UpdatedAt.Ticks, ParseTicks(read.Headers.ETag!.ToString()));
    }

    [Fact]
    public async Task ArchiveAndRestore_BackToBack_NeverReuseAPublishedRevision()
    {
        var (client, projectId) = await CreateProjectWithClientAsync();
        var published = new List<long>();

        for (var round = 0; round < 4; round++)
        {
            foreach (var path in new[] { "archive", "restore" })
            {
                using var response = await PostAsync(client, $"/api/projects/{projectId}/{path}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var dto = await response.Content.ReadFromJsonAsync<ProjectDto>();
                Assert.NotNull(dto);
                published.Add(dto.UpdatedAt.Ticks);
            }
        }

        Assert.Equal(8, published.Distinct().Count());
        Assert.Equal(published.OrderBy(item => item), published);

        // The revision the row ends on is the one the read publishes, and it is the highest of them
        // all: no archive/restore pair ever went backwards.
        var live = await ReadRevisionAsync(client, projectId);
        Assert.Equal(published.Max(), live.Ticks);
    }

    [Fact]
    public async Task Archive_KeepsStatusLastWriteWins_AndStillRefusesAnEditBasedOnASupersededRevision()
    {
        var (client, otherClient, project) = await CreateProjectWithTwoClientsAsync();
        var baseToken = FormatIfMatchToken(await ReadRevisionAsync(client, project.Id));

        // An honest editor keeps its declared base…
        var held = await PatchAsync(client, project.Id, baseToken, Form(project, name: "Held rename"));
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        var heldToken = held.Headers.ETag;
        Assert.NotNull(heldToken);

        // …while a second session archives the project, which is a status flip, not a conflict.
        using (var archive = await PostAsync(otherClient, $"/api/projects/{project.Id}/archive"))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
            var archived = await archive.Content.ReadFromJsonAsync<ProjectDto>();
            Assert.NotNull(archived);
            Assert.Equal(ProjectStatuses.Archived, archived.Status);
        }

        // The archived project is still editable by its member (archive is not a lock), but the
        // revision the first editor holds is now stale: the fence has to stay armed across the
        // status write rather than being relaxed because archive went through the same helper.
        var stale = await PatchAsync(client, project.Id, heldToken!.ToString(), Form(project, description: "Must not land"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var live = await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(live);
        Assert.Equal("Held rename", live.Name);
        Assert.Equal(project.Description, live.Description);
        Assert.Equal(ProjectStatuses.Archived, live.Status);
    }

    [Fact]
    public async Task ArchiveAndRestore_ByANonMember_AreRefusedAndLeaveTheRevisionAlone()
    {
        var (owner, _, project) = await CreateProjectWithTwoClientsAsync();
        var outsider = await CreateOutsiderAsync("revision-outsider");
        var before = await ReadRevisionAsync(owner, project.Id);

        // This endpoint answers a non-member with the same 404 it uses for a missing project, which
        // predates this change and is deliberately kept: it does not confirm the project exists.
        using (var archive = await PostAsync(outsider, $"/api/projects/{project.Id}/archive"))
        {
            Assert.Equal(HttpStatusCode.NotFound, archive.StatusCode);
        }
        using (var restore = await PostAsync(outsider, $"/api/projects/{project.Id}/restore"))
        {
            Assert.Equal(HttpStatusCode.NotFound, restore.StatusCode);
        }

        // The write path moved from SaveChanges to a stamped UPDATE; the membership check that
        // guards it is unchanged and still decided before anything is written.
        Assert.Equal(before, await ReadRevisionAsync(owner, project.Id));
        var after = await owner.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(after);
        Assert.Equal(ProjectStatuses.Active, after.Status);
    }

    // -----------------------------------------------------------------------------------------------
    // Wire-level unconditional forms: the behaviour each conditional flavor promises.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WildcardAndLegacyWrites_AppliedOverAFutureCommittedRevision_MoveItForward_AndStayDistinct()
    {
        var (client, projectId) = await CreateProjectWithClientAsync();
        var observed = await ReadRevisionAsync(client, projectId);
        var committed = observed.AddSeconds(30);
        await AdvanceRowExternallyAsync(projectId, committed, "Committed by the intervening writer");

        var stamps = new List<long>();
        for (var round = 0; round < 3; round++)
        {
            using var wildcard = await PatchAsync(client, projectId, "*", Form(await ReadDtoAsync(client, projectId), name: $"Forced {round}"));
            Assert.Equal(HttpStatusCode.OK, wildcard.StatusCode);
            stamps.Add(ParseTicks(wildcard.Headers.ETag!.ToString()));

            using var legacy = await PatchAsync(client, projectId, null, Form(await ReadDtoAsync(client, projectId), description: $"Legacy {round}"));
            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
            stamps.Add(ParseTicks(legacy.Headers.ETag!.ToString()));
        }

        // Wildcard keeps its RFC 9110 meaning and a header-less legacy client keeps last-write-wins:
        // neither is asked to recover from a conflict, and neither is allowed to hand back a stamp
        // that is already in circulation.
        Assert.Equal(6, stamps.Distinct().Count());
        Assert.Equal(stamps.OrderBy(item => item), stamps);
        Assert.All(stamps, stamp => Assert.True(stamp % 10 == 0, "published stamps stay on the store's grid"));
        Assert.True(stamps[0] > committed.Ticks, "the first unconditional write must not land on or behind the revision already committed");
    }

    [Fact]
    public async Task UnconditionalWrites_MixedWithCommittedEdits_NeverAnswerAConflict()
    {
        var (client, projectId) = await CreateProjectWithClientAsync();
        var project = await ReadDtoAsync(client, projectId);
        var published = new List<long>();

        // Each round lets another writer commit ahead of the next request, then applies an
        // unconditional form over the displaced row. A legacy client declares no base, so it has
        // nothing to merge and no recovery path: pre-fix its UPDATE was fenced on the revision the
        // request had read, so any request losing that race answered 409. The wildcard form, which
        // promises to apply over whatever is there, was fenced the other way round — unconditionally
        // — and could therefore publish the revision it read instead of the one it landed on.
        for (var round = 0; round < 5; round++)
        {
            var live = await ReadRevisionAsync(client, projectId);
            await AdvanceRowExternallyAsync(projectId, live.AddSeconds(5), $"Intervening commit {round}");

            var headerless = round % 2 == 0;
            using var response = await PatchAsync(client, projectId, headerless ? null : "*",
                Form(await ReadDtoAsync(client, projectId), name: headerless ? $"Legacy {round}" : $"Forced {round}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = await response.Content.ReadFromJsonAsync<ProjectDto>();
            Assert.NotNull(dto);
            published.Add(dto!.UpdatedAt.Ticks);
        }

        Assert.Equal(5, published.Distinct().Count());
        Assert.Equal(published.OrderBy(item => item), published);
        var final = await ReadRevisionAsync(client, projectId);
        Assert.Equal(published.Max(), final.Ticks);
    }

    // -----------------------------------------------------------------------------------------------
    // Production store: Projects.UpdatedAt is `timestamp with time zone`, i.e. microsecond digits.
    // -----------------------------------------------------------------------------------------------

    [DisposablePostgresFact]
    public async Task Postgres_StampsPublishedByStampedWrites_RoundTripThroughTheColumnWithoutRoundingDrift()
    {
        await using var scoped = await RevisionSchema.CreateAsync("roundtrip");
        var db = scoped.Db!;
        var projectId = await SeedProjectAsync(db, "Postgres grid", new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc).AddTicks(123_456_705));

        var observed = await ReadRevisionAsync(db, projectId);
        Assert.Equal(0, observed.Ticks % 10); // the column already dropped the half microsecond

        for (var round = 0; round < 10; round++)
        {
            // The seeded row deliberately carries a half-microsecond tail, and the production column
            // drops it on write. Every stamp the stamped write publishes has to arrive back from the
            // column unchanged — otherwise the ETag handed to the client is not the revision the next
            // honest If-Match compares against, which is the failure the pre-fix paths could hit
            // whenever they wrote a raw `DateTime.UtcNow` with its 100 ns digits.
            var landed = await WriteNameAsync(db, projectId, observed, RevisionFence.LiveRow, $"Round {round}");
            Assert.Equal(RevisionWriteOutcome.Applied, landed.Outcome);

            var stored = await ReadRevisionAsync(db, projectId);
            Assert.Equal(landed.Revision.Ticks, stored.Ticks);
            Assert.True(ProjectRevision.TryParseETag(ProjectRevision.FormatETag(landed.Revision), out var reparsed));
            Assert.Equal(stored.Ticks, reparsed.Ticks);
            Assert.Equal(0, stored.Ticks % 10);
            observed = stored;
        }
    }

    [DisposablePostgresFact]
    public async Task Postgres_UnconditionalWriters_StartingFromOneObservedRevision_BothLandWithDistinctStamps()
    {
        await using var scoped = await RevisionSchema.CreateAsync("race");
        var projectId = await SeedProjectAsync(scoped.Db!, "Postgres race", DateTime.UtcNow);

        // Two real sessions, two pooled connections, one row, the same starting revision: this is
        // the wildcard pile-up the pre-fix unconditional UPDATE could not survive, because both
        // statements computed the identical stamp from their shared base and the second simply
        // re-published the first one's revision.
        var sharedBase = await ReadRevisionAsync(scoped.Db!, projectId);
        var first = await WriteNameAsync(scoped.Db!, projectId, sharedBase, RevisionFence.LiveRow, "First landing");
        await using var secondContext = scoped.CreateContext();
        var second = await WriteNameAsync(secondContext, projectId, sharedBase, RevisionFence.LiveRow, "Second landing");

        Assert.Equal(RevisionWriteOutcome.Applied, first.Outcome);
        Assert.Equal(RevisionWriteOutcome.Applied, second.Outcome);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.True(second.Revision > first.Revision);

        var row = await ReadRevisionAsync(secondContext, projectId);
        Assert.Equal(second.Revision.Ticks, row.Ticks);
        Assert.Equal("Second landing", (await secondContext.Projects.AsNoTracking().SingleAsync(item => item.Id == projectId)).Name);
    }

    // -----------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------

    private static Task<RevisionWriteResult> WriteNameAsync(
        TaslimDbContext db,
        Guid projectId,
        DateTime observedRevision,
        RevisionFence fence,
        string name) =>
        ProjectRevision.WriteStampedAsync(
            db,
            projectId,
            observedRevision,
            fence,
            (target, nextRevision) => target.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Name, name)
                .SetProperty(item => item.UpdatedAt, nextRevision)),
            CancellationToken.None);

    /// <summary>Advances the row past <paramref name="observed"/>, standing in for the commit that lands while another writer is mid-flight.</summary>
    private static async Task<DateTime> CommitAheadOfAsync(TaslimDbContext db, Guid projectId, DateTime observed, string description)
    {
        var committed = ProjectRevision.NextStamp(observed, observed.AddSeconds(1));
        await db.Projects.Where(item => item.Id == projectId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Description, description)
                .SetProperty(item => item.UpdatedAt, committed));
        return committed;
    }

    private static async Task<DateTime> ReadRevisionAsync(TaslimDbContext db, Guid projectId) =>
        await db.Projects.AsNoTracking().Where(item => item.Id == projectId).Select(item => item.UpdatedAt).SingleAsync();

    private static async Task<Guid> SeedProjectAsync(TaslimDbContext db, string name, DateTime updatedAt)
    {
        var now = DateTime.UtcNow;
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = $"Revision workspace {Guid.NewGuid():N}",
            Slug = $"revision-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Name = name,
            Description = "Original synopsis",
            Type = "Business",
            Status = ProjectStatuses.Active,
            CreatedAt = now,
            UpdatedAt = updatedAt,
        };
        db.Workspaces.Add(workspace);
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<Guid> SeedProjectAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        return await SeedProjectAsync(scope.ServiceProvider.GetRequiredService<TaslimDbContext>(), name, DateTime.UtcNow);
    }

    /// <summary>Advances the row through a separate context, i.e. a write this request never observed.</summary>
    private async Task AdvanceRowExternallyAsync(Guid projectId, DateTime revision, string description)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        await db.Projects.Where(item => item.Id == projectId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Description, description)
                .SetProperty(item => item.UpdatedAt, revision));
    }

    private async Task<(HttpClient Client, Guid ProjectId)> CreateProjectWithClientAsync()
    {
        var (client, _, project) = await CreateProjectWithTwoClientsAsync();
        return (client, project.Id);
    }

    private async Task<(HttpClient EditorA, HttpClient EditorB, ProjectDto Project)> CreateProjectWithTwoClientsAsync()
    {
        var email = $"revision-{Guid.NewGuid():N}@example.com";

        using var registrar = factory.CreateClient();
        var registration = await SendWithCsrfAsync(registrar, HttpMethod.Post, "/api/auth/register",
            new { displayName = "Revision Owner", email, password = Password, preferredLanguage = "en" });
        Assert.True(registration.IsSuccessStatusCode, await registration.Content.ReadAsStringAsync());
        var auth = (await registration.Content.ReadFromJsonAsync<AuthResponse>())!;

        var editorA = factory.CreateClient();
        await LoginAsync(editorA, email);
        var token = await GetCsrfAsync(editorA);
        var project = await SendWithTokenAsync<ProjectDto>(editorA, HttpMethod.Post,
            $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", token,
            new { name = "Shared Campaign", description = "Original synopsis", instructions = "Original notes", contextNotes = "Original context", type = "Business" });

        var editorB = factory.CreateClient();
        await LoginAsync(editorB, email);
        return (editorA, editorB, project);
    }

    private async Task<HttpClient> CreateOutsiderAsync(string label)
    {
        var outsider = factory.CreateClient();
        var response = await SendWithCsrfAsync(outsider, HttpMethod.Post, "/api/auth/register",
            new { displayName = "Not A Member", email = $"{label}-{Guid.NewGuid():N}@example.com", password = Password, preferredLanguage = "en" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return outsider;
    }

    private static async Task<ProjectDto> ReadDtoAsync(HttpClient client, Guid projectId)
    {
        var dto = await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{projectId}");
        Assert.NotNull(dto);
        return dto;
    }

    private static async Task<DateTime> ReadRevisionAsync(HttpClient client, Guid projectId) =>
        (await ReadDtoAsync(client, projectId)).UpdatedAt;

    private static object Form(ProjectDto project, string? name = null, string? description = null) => new
    {
        name = name ?? project.Name,
        description = description ?? project.Description,
        instructions = project.Instructions,
        contextNotes = project.ContextNotes,
        type = project.Type,
    };

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid projectId, string? ifMatch, object body)
    {
        using var request = await BuildPatchAsync(client, projectId, ifMatch, body);
        return await client.SendAsync(request);
    }

    private static async Task<HttpRequestMessage> BuildPatchAsync(HttpClient client, Guid projectId, string? ifMatch, object body)
    {
        var token = await GetCsrfAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/projects/{projectId}");
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path)
    {
        var token = await GetCsrfAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static string FormatIfMatchToken(DateTime revision) =>
        "\"" + revision.ToString("O", CultureInfo.InvariantCulture) + "\"";

    private static long ParseTicks(string etagHeader)
    {
        Assert.True(ProjectRevision.TryParseETag(etagHeader, out var revision), $"unparseable ETag token: {etagHeader}");
        return revision.Ticks;
    }

    private static async Task LoginAsync(HttpClient client, string email)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/login", new { email, password = Password });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return response.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object payload)
    {
        var token = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<T> SendWithTokenAsync<T>(HttpClient client, HttpMethod method, string path, string token, object payload)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>
    /// A disposable PostgreSQL schema holding the real <c>Projects</c> table, so the stamp rules are
    /// exercised against <c>timestamp with time zone</c> — the production column — rather than only
    /// against the in-memory provider that hides its rounding behaviour. Skipped unless API CI's
    /// PostgreSQL service is configured.
    /// </summary>
    private sealed class RevisionSchema : IAsyncDisposable
    {
        public TaslimDbContext? Db { get; private set; }
        private DbContextOptions<TaslimDbContext> Options { get; } = null!;
        private string Schema { get; }

        private RevisionSchema(DbContextOptions<TaslimDbContext> options, string schema)
        {
            Options = options;
            Schema = schema;
        }

        public TaslimDbContext CreateContext() => new(Options);

        public static async Task<RevisionSchema> CreateAsync(string label)
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
            var schema = $"revision_fence_{label}_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE SCHEMA \"{schema}\"";
                await create.ExecuteNonQueryAsync();
            }
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scoped.ConnectionString).Options;
            var instance = new RevisionSchema(options, schema);
            instance.Db = instance.CreateContext();
            await instance.Db.Database.EnsureCreatedAsync();
            return instance;
        }

        public async ValueTask DisposeAsync()
        {
            if (Db is not null) await Db.DisposeAsync();
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>
/// Unit pins for the store-resolution floor <see cref="ProjectRevision.NextStamp"/> applies. The
/// clock the endpoint reads carries 100 ns ticks while every supported column keeps microseconds,
/// so a stamp that is "unique" in memory can still collapse onto a value the store already holds.
/// </summary>
public sealed class ProjectRevisionStoreResolutionTests
{
    private static readonly DateTime Base = new DateTime(2026, 11, 24, 9, 0, 0, DateTimeKind.Unspecified).AddTicks(123_456_700);

    [Theory]
    [InlineData(1)]   // a clock a single tick ahead is indistinguishable once persisted
    [InlineData(5)]   // half a microsecond
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(-1)]
    public void NextStamp_IgnoresSubMicrosecondClockAdvances_AndStillClearsTheBase(int ticks)
    {
        var published = ProjectRevision.NextStamp(Base, Base.AddTicks(ticks));
        Assert.Equal(Base.AddTicks(10), published);
    }

    [Fact]
    public void NextStamp_DropsDigitsTheStoreWouldRound_AndKeepsTheStrictAdvance()
    {
        var ahead = Base.AddTicks(25); // 2.5 microseconds past the base
        Assert.Equal(Base.AddTicks(20), ProjectRevision.NextStamp(Base, ahead));

        var unalignedBase = Base.AddTicks(5);
        var unalignedPublished = ProjectRevision.NextStamp(unalignedBase, unalignedBase);
        Assert.True(unalignedPublished > unalignedBase);
        Assert.Equal(0, unalignedPublished.Ticks % 10);
    }

    [Fact]
    public void NextStamp_KeepsTheKindThatTheProductionColumnRequires()
    {
        // Npgsql refuses to bind Kind=Unspecified to `timestamp with time zone`, and the stamp the
        // endpoint publishes is bound back into the same column on the next write.
        var utcBase = DateTime.SpecifyKind(Base, DateTimeKind.Utc);
        var published = ProjectRevision.NextStamp(utcBase, utcBase);
        Assert.Equal(DateTimeKind.Utc, published.Kind);
        Assert.Equal(utcBase.AddTicks(10), published);
    }

    [Fact]
    public void StampedSequence_StaysUniqueAfterStoreRounding()
    {
        // Twelve writes inside one clock tick, measured the way the store measures them: whatever
        // survives rounding must still be twelve distinct revisions.
        var stamp = Base;
        var persisted = new List<DateTime>();
        for (var round = 0; round < 12; round++)
        {
            stamp = ProjectRevision.NextStamp(stamp, Base);
            persisted.Add(stamp);
        }
        Assert.Equal(12, persisted.Distinct().Count());
        Assert.Equal(persisted.OrderBy(item => item), persisted);
    }
}
