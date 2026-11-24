using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Two editors hold the same project open (two tabs of one workspace member).
/// Each one edits from the revision it last read. The endpoint must not let the
/// second write silently discard what the first write already committed.
/// The interleaving is expressed through the revision each client declares,
/// so every scenario here is deterministic rather than thread-timed.
/// </summary>
public sealed class ProjectPatchRevisionConflictTests : IClassFixture<TaslimApiFactory>
{
    private const string Password = "StrongPassword!234";
    private readonly TaslimApiFactory factory;

    public ProjectPatchRevisionConflictTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task ConcurrentEditors_BothEditFromSameBase_SecondWriteIsRejected_AndFirstSurvivesIntact()
    {
        var (editorA, editorB, project) = await CreateProjectWithTwoTabsAsync();
        var baseETag = FormatIfMatchToken(await ReadRevisionAsync(editorA, project.Id));

        // Editor B saves its synopsis first: its declared base is still current.
        var bSave = await PatchAsync(editorB, project.Id, baseETag, Form(project, description: "B's synopsis edit"));
        Assert.Equal(HttpStatusCode.OK, bSave.StatusCode);

        // Editor A now saves its title edit, declaring the same (now stale) base.
        var aSave = await PatchAsync(editorA, project.Id, baseETag, Form(project, name: "A's renamed project"));

        // The defect: on the unfenced endpoint this write returns 200 and silently
        // reverts B's synopsis to A's stale copy — a lost update with no signal.
        Assert.Equal(HttpStatusCode.Conflict, aSave.StatusCode);
        var envelope = await aSave.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PROJECT_REVISION_CONFLICT", envelope.GetProperty("error").GetProperty("code").GetString());

        // The rejected write must not land at all: B's committed synopsis survives.
        var after = await editorA.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(after);
        Assert.Equal("B's synopsis edit", after.Description);
        Assert.Equal(project.Name, after.Name);
    }

    [Fact]
    public async Task ConcurrentEditors_StaleWriteRetriedAgainstConflictHandedRevision_IsApplied()
    {
        var (editorA, editorB, project) = await CreateProjectWithTwoTabsAsync();
        var baseETag = FormatIfMatchToken(await ReadRevisionAsync(editorA, project.Id));

        var bSave = await PatchAsync(editorB, project.Id, baseETag, Form(project, description: "B's synopsis edit"));
        Assert.Equal(HttpStatusCode.OK, bSave.StatusCode);

        var rejected = await PatchAsync(editorA, project.Id, baseETag, Form(project, name: "A's renamed project"));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        // Recovery path: the conflict response hands A the fresh revision, so the
        // merged intent can be re-applied without a blind extra read.
        var freshToken = rejected.Headers.ETag;
        Assert.NotNull(freshToken);
        var merged = await PatchAsync(editorA, project.Id, freshToken.ToString(), Form(project, name: "A's renamed project", description: "B's synopsis edit"));
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        var after = await editorA.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(after);
        Assert.Equal("A's renamed project", after.Name);
        Assert.Equal("B's synopsis edit", after.Description);
    }

    [Fact]
    public async Task RevisionIsDiscoverable_GetExposesCurrentRevision_AndFreshWriteIsApplied()
    {
        var (editorA, _, project) = await CreateProjectWithTwoTabsAsync();

        using var get = await editorA.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        // A client must be able to learn the revision it edits against from the
        // response itself. On the unfenced endpoint no ETag is emitted at all.
        var etag = get.Headers.ETag;
        Assert.NotNull(etag);

        var saved = await PatchAsync(editorA, project.Id, etag!.ToString(), Form(project, name: "Solo rename"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var after = await saved.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(after);
        Assert.Equal("Solo rename", after.Name);
        Assert.True(after.UpdatedAt > project.UpdatedAt, "every accepted write must strictly advance the revision");

        using var getAfter = await editorA.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, getAfter.StatusCode);
        Assert.NotEqual(etag.ToString(), getAfter.Headers.ETag!.ToString());

        // The header the endpoint hands back must be re-usable verbatim as If-Match on
        // whichever store serves the row. A stamp read back in the production term
        // (Kind=Utc, from `timestamp with time zone`) used to gain a second UTC
        // designator here, so the very token the fence publishes was unparseable.
        var handedBack = getAfter.Headers.ETag!.ToString();
        Assert.DoesNotContain("ZZ", handedBack);
        Assert.True(Taslim.Api.Projects.ProjectRevision.TryParseETag(handedBack, out var parsedHanded));
        var reread = await editorA.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(reread);
        Assert.Equal(reread!.UpdatedAt.Ticks, parsedHanded.Ticks);
    }

    [Fact]
    public async Task WildcardIfMatch_DeclaresWriteWhateverIsThere_IsAlwaysApplied()
    {
        var (editorA, editorB, project) = await CreateProjectWithTwoTabsAsync();
        var baseETag = FormatIfMatchToken(await ReadRevisionAsync(editorA, project.Id));

        var bSave = await PatchAsync(editorB, project.Id, baseETag, Form(project, description: "B's synopsis edit"));
        Assert.Equal(HttpStatusCode.OK, bSave.StatusCode);

        var forced = await PatchAsync(editorA, project.Id, "*", Form(project, name: "A's forced rename", description: "B's synopsis edit"));
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);

        var after = await editorA.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(after);
        Assert.Equal("A's forced rename", after.Name);
        Assert.Equal("B's synopsis edit", after.Description);
    }

    [Fact]
    public async Task InvalidIfMatchToken_IsRefusedAsBadRequest_WithoutTouchingTheProject()
    {
        var (editorA, _, project) = await CreateProjectWithTwoTabsAsync();

        var response = await PatchAsync(editorA, project.Id, "\"not-a-revision\"", Form(project, name: "Must not apply"));

        // Without validation the token is simply ignored and the write lands.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_IF_MATCH", envelope.GetProperty("error").GetProperty("code").GetString());

        var after = await editorA.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(after);
        Assert.Equal(project.Name, after.Name);
    }

    [Fact]
    public async Task LegacyClientWithoutIfMatch_KeepsLastWriteWinsSemantics()
    {
        var (editorA, editorB, project) = await CreateProjectWithTwoTabsAsync();

        var first = await PatchAsync(editorA, project.Id, null, Form(project, name: "Legacy title"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await PatchAsync(editorB, project.Id, null, Form(project, name: "Legacy title", description: "Legacy synopsis"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var after = await editorA.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(after);
        Assert.Equal("Legacy synopsis", after.Description);
    }

    [Fact]
    public async Task NonMember_CannotDiscoverTheRevision_StaleOrWildcardWriteIsRefusedBeforeTheFence()
    {
        var (editorA, _, project) = await CreateProjectWithTwoTabsAsync();
        var outsider = await CreateOutsiderAsync("patch-outsider");

        // Membership is decided before any conditional-request handling: an outsider
        // must not be able to read the stamp, nor use a stale, malformed or wildcard
        // If-Match to learn anything beyond "you are not a member".
        using (var probe = await outsider.GetAsync($"/api/projects/{project.Id}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, probe.StatusCode);
            Assert.False(probe.Headers.Contains("ETag"));
        }

        var stale = await PatchAsync(outsider, project.Id, "\"2020-01-01T00:00:00.0000000Z\"", Form(project, name: "Outsider rename"));
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);

        var junk = await PatchAsync(outsider, project.Id, "\"not-a-revision\"", Form(project, name: "Outsider rename"));
        Assert.Equal(HttpStatusCode.Forbidden, junk.StatusCode);

        var forced = await PatchAsync(outsider, project.Id, "*", Form(project, name: "Outsider rename"));
        Assert.Equal(HttpStatusCode.Forbidden, forced.StatusCode);

        var after = await editorA.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(after);
        Assert.Equal(project.Name, after.Name);
    }

    [Fact]
    public async Task GuardedUpdate_FencedOnASupersededRevision_TouchesNoRowsAndLeavesTheCommittedFields()
    {
        // The last line of defence against a commit that lands between the endpoint's own
        // read and its write is the conditional UPDATE, not the header comparison that
        // already passed. Its store-level semantics cannot be scheduled deterministically
        // over HTTP, so the identical fenced write is replayed here against a revision that
        // a second writer has demonstrably advanced: zero rows affected, nothing overwritten.
        var (editorA, _, project) = await CreateProjectWithTwoTabsAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        var observed = await db.Projects.AsNoTracking()
            .Where(item => item.Id == project.Id)
            .Select(item => item.UpdatedAt)
            .SingleAsync();
        var advanced = await db.Projects
            .Where(item => item.Id == project.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Description, "Committed by the second writer")
                .SetProperty(item => item.UpdatedAt, Taslim.Api.Projects.ProjectRevision.NextStamp(observed, observed)));
        Assert.Equal(1, advanced);

        var fenced = await db.Projects
            .Where(item => item.Id == project.Id && item.UpdatedAt == observed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Name, "Must not land")
                .SetProperty(item => item.Description, "Must not land either")
                .SetProperty(item => item.UpdatedAt, DateTime.UtcNow));
        Assert.Equal(0, fenced);

        var survivor = await editorA.GetFromJsonAsync<ProjectDto>($"/api/projects/{project.Id}");
        Assert.NotNull(survivor);
        Assert.Equal(project.Name, survivor!.Name);
        Assert.Equal("Committed by the second writer", survivor.Description);
    }

    private async Task<(HttpClient EditorA, HttpClient EditorB, ProjectDto Project)> CreateProjectWithTwoTabsAsync()
    {
        var email = $"patch-conflict-{Guid.NewGuid():N}@example.com";

        // Session 1 registers the owner and creates the project.
        using var registrar = factory.CreateClient();
        var registration = await SendWithCsrfAsync(registrar, HttpMethod.Post, "/api/auth/register",
            new { displayName = "Conflict Owner", email, password = Password, preferredLanguage = "en" });
        Assert.True(registration.IsSuccessStatusCode, await registration.Content.ReadAsStringAsync());
        var auth = (await registration.Content.ReadFromJsonAsync<AuthResponse>())!;

        // Editor tab A: fresh client, authenticated via login.
        var editorA = factory.CreateClient();
        await LoginAsync(editorA, email);
        var createToken = await GetCsrfAsync(editorA);
        var project = await SendWithTokenAsync<ProjectDto>(editorA, HttpMethod.Post,
            $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", createToken,
            new { name = "Shared Campaign", description = "Original synopsis", instructions = "Original notes", contextNotes = "Original context", type = "Business" });

        // Editor tab B: second client with its own cookie jar for the same member.
        var editorB = factory.CreateClient();
        await LoginAsync(editorB, email);
        return (editorA, editorB, project);
    }

    /// <summary>An authenticated account that has no membership in the edited project's workspace.</summary>
    private async Task<HttpClient> CreateOutsiderAsync(string label)
    {
        var outsider = factory.CreateClient();
        var response = await SendWithCsrfAsync(outsider, HttpMethod.Post, "/api/auth/register",
            new { displayName = "Not A Member", email = $"{label}-{Guid.NewGuid():N}@example.com", password = Password, preferredLanguage = "en" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return outsider;
    }

    private static object Form(ProjectDto project, string? name = null, string? description = null) => new
    {
        name = name ?? project.Name,
        description = description ?? project.Description,
        instructions = project.Instructions,
        contextNotes = project.ContextNotes,
        type = project.Type,
    };

    private async Task LoginAsync(HttpClient client, string email)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/login", new { email, password = Password });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<DateTime> ReadRevisionAsync(HttpClient client, Guid projectId)
    {
        var project = await client.GetFromJsonAsync<ProjectDto>($"/api/projects/{projectId}");
        Assert.NotNull(project);
        return project.UpdatedAt;
    }

    private static string FormatIfMatchToken(DateTime revision) =>
        "\"" + revision.ToString("O", CultureInfo.InvariantCulture) + "\"";

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid projectId, string? ifMatch, object body)
    {
        var token = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/projects/{projectId}");
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
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
}

/// <summary>
/// Deterministic unit pins for the revision-stamp rules the HTTP flow relies on.
/// The same-tick advance cannot be exercised through the wire (wall-clock luck),
/// so it is pinned directly here.
/// </summary>
public sealed class ProjectRevisionStampTests
{
    private static readonly DateTime Base = new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Unspecified).AddTicks(123_456_700);

    [Fact]
    public void NextStamp_WithinSameTick_StillAdvancesByTheFinestPersistableUnit()
    {
        var advanced = Taslim.Api.Projects.ProjectRevision.NextStamp(Base, Base);
        Assert.True(advanced > Base);
        Assert.Equal(Base.AddTicks(10), advanced); // one microsecond, the Postgres floor
    }

    [Fact]
    public void NextStamp_ClockBehindBase_StillAdvancesMonotonically()
    {
        Assert.Equal(Base.AddTicks(10), Taslim.Api.Projects.ProjectRevision.NextStamp(Base, Base.AddTicks(-500)));
    }

    [Fact]
    public void NextStamp_ClockAheadOfBase_UsesTheClockVerbatim()
    {
        var now = Base.AddTicks(1_000_000);
        Assert.Equal(now, Taslim.Api.Projects.ProjectRevision.NextStamp(Base, now));
    }

    [Fact]
    public void StampSequence_IsStrictlyIncreasingEvenWithinOneTick()
    {
        var stamp = Base;
        var stamps = new List<DateTime>();
        for (var editor = 0; editor < 5; editor++)
        {
            // Five back-to-back guarded writes on a frozen clock (the worst case).
            stamp = Taslim.Api.Projects.ProjectRevision.NextStamp(stamp, Base);
            stamps.Add(stamp);
        }
        Assert.Equal(stamps.OrderBy(item => item), stamps);
        Assert.Equal(5, stamps.Distinct().Count());
    }

    [Fact]
    public void FormattedETag_RoundTripsToTheSameTicks_RegardlessOfQuotingOrZoneForm()
    {
        var formatted = Taslim.Api.Projects.ProjectRevision.FormatETag(Base);
        Assert.True(Taslim.Api.Projects.ProjectRevision.TryParseETag(formatted, out var strong));
        Assert.Equal(Base, strong);

        Assert.True(Taslim.Api.Projects.ProjectRevision.TryParseETag(Base.ToString("O", CultureInfo.InvariantCulture), out var bare));
        Assert.Equal(Base, bare);

        Assert.True(Taslim.Api.Projects.ProjectRevision.TryParseETag("W/" + formatted, out var weak));
        Assert.Equal(Base, weak);

        var zoned = DateTime.SpecifyKind(Base, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
        Assert.True(Taslim.Api.Projects.ProjectRevision.TryParseETag("\"" + zoned + "\"", out var offset));
        Assert.Equal(Base, offset);
    }

    [Fact]
    public void Wildcards_Junk_And_Lists_AreNotParsedAsRevisions()
    {
        Assert.False(Taslim.Api.Projects.ProjectRevision.TryParseETag("*", out _));
        Assert.False(Taslim.Api.Projects.ProjectRevision.TryParseETag("not-a-revision", out _));
        Assert.False(Taslim.Api.Projects.ProjectRevision.TryParseETag("\"a\",\"b\"", out _));
        Assert.False(Taslim.Api.Projects.ProjectRevision.TryParseETag("  ", out _));
    }

    [Fact]
    public void FormattedETag_WritesTheUtcDesignatorExactlyOnce_ForEveryKindTheStoreCanReturn()
    {
        // `timestamp with time zone` — the production column behind Projects.UpdatedAt —
        // hands the stamp back as Kind=Utc, while the in-memory test store hands back
        // Kind=Unspecified. The round-trip format already carries the designator for the
        // UTC term, so appending it once more published an unparseable token and every
        // honest If-Match was refused as INVALID_IF_MATCH. That is invisible through the
        // SQLite-backed flow alone, so both terms are pinned here.
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Utc })
        {
            var token = Taslim.Api.Projects.ProjectRevision.FormatETag(DateTime.SpecifyKind(Base, kind));
            Assert.EndsWith("Z\"", token);
            Assert.DoesNotContain("ZZ", token);
            Assert.True(Taslim.Api.Projects.ProjectRevision.TryParseETag(token, out var parsed));
            Assert.Equal(Base.Ticks, parsed.Ticks);
        }

        // A local-kind stamp is brought back to the same instant before the designator is
        // written, so the token never claims a UTC instant the row does not hold.
        var local = DateTime.SpecifyKind(Base, DateTimeKind.Local);
        Assert.True(Taslim.Api.Projects.ProjectRevision.TryParseETag(
            Taslim.Api.Projects.ProjectRevision.FormatETag(local), out var fromLocal));
        Assert.Equal(local.ToUniversalTime().Ticks, fromLocal.Ticks);
    }

    [Fact]
    public void ParsedRevision_IsUnspecifiedKind_MatchingTheStoredColumnTerm()
    {
        Assert.True(Taslim.Api.Projects.ProjectRevision.TryParseETag(
            "\"" + DateTime.SpecifyKind(Base, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) + "\"", out var parsed));
        Assert.Equal(DateTimeKind.Unspecified, parsed.Kind);
    }
}
