using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA companion for #823 (issue #698 release preparation): pins the
/// fail-closed contract of the default-off <c>MovieVideoMakeoverSource</c> admission
/// gate on the HTTP verb surface that the owner-side tests do not cover.
///
/// The existing disabled-deployment contract (see
/// <see cref="MovieVideoMakeoverSourceDisabledApiTests"/>) proves both GETs are
/// opaque 404s on an unconfigured host. This class proves the complementary half:
/// with the gate unset, no non-GET verb - authenticated, CSRF-valid, owner or
/// outsider, or anonymous - can (a) disclose preview/source metadata, ids, totals,
/// or the read-model marker, or (b) admit any provider work: zero generation-job
/// rows are created and the seeded source rows are untouched after the full
/// verb storm. Everything runs against an isolated synthetic in-process host
/// (shared <see cref="GenerationJobsNoWorkerFactory"/>, ephemeral SQLite file,
/// worker disabled, Testing environment): no production HTTP, no paid provider,
/// no user data, no configuration mutation.
///
/// Test-only. Adds no product behaviour; if it fails, the product changed.
/// </summary>
/// <summary>
/// Dedicated serial collection: this class boots an additional full in-process host, and the
/// repository pins CPU-contention-sensitive GenerationJobs timing tests to
/// non-parallel collections (see MovieProviderFakeFinalization et al.). Running
/// this class exclusively keeps the suite deterministic without touching any
/// existing collection.
/// </summary>
[CollectionDefinition("MovieMakeoverSourceDisabledVerbSurface", DisableParallelization = true)]
public sealed class MovieMakeoverSourceDisabledVerbSurfaceCollectionDefinition { }

[Collection("MovieMakeoverSourceDisabledVerbSurface")]
public sealed class MovieVideoMakeoverSourceDisabledVerbSurfaceApiTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;
    private readonly ITestOutputHelper output;

    public MovieVideoMakeoverSourceDisabledVerbSurfaceApiTests(
        GenerationJobsNoWorkerFactory factory,
        ITestOutputHelper output)
    {
        this.factory = factory;
        this.output = output;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// The exact shape a would-be "admitted provider work" submission would carry
    /// (provider name, source clip, generation intent). None of these fields is
    /// ever read by the read-model controller - there is no POST action at all -
    /// but the payload lets the test fail loudly if a future change ever wires a
    /// mutation verb to the makeover source route while the gate is off.
    /// </summary>
    private static object ProviderWorkPayload(Guid projectId, Guid clipId) => new
    {
        provider = "movie-video",
        action = "generate",
        projectId,
        sourceClipId = clipId,
        costAdmission = true,
    };

    [Theory] // CSRF-valid, fully authenticated owner attempts every unsafe verb.
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Authenticated_owner_with_csrf_never_admits_work_or_metadata_on_disabled_verb_surface(string method)
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Disabled verb surface project");
        var (clipId, assetId, fileId) = await SeedHealthySourceAsync(client, owner, projectId);

        var jobCountBefore = await CountJobsAsync();
        Assert.Equal(0, jobCountBefore); // nothing on this ephemeral host is admitted pre-flight

        var payload = ProviderWorkPayload(projectId, clipId);
        foreach (var path in new[] { ListPath(projectId), $"{ListPath(projectId)}/{clipId}" })
        {
            var response = await SendWithCsrfAsync(client, new HttpMethod(method), path, payload);
            var body = await response.Content.ReadAsStringAsync();

            // Fail-closed only: never a success/redirect answer from the disabled host.
            Assert.True(
                (int)response.StatusCode is >= 400 and < 500,
                $"{method} {path} returned {(int)response.StatusCode} {response.StatusCode}; expected a 4xx fail-closed status on an unconfigured deployment. Body: {body}");

            // No source/preview metadata or existence markers may leak on any verb.
            Assert.False(response.Headers.Contains("X-Movie-Read-Model"), $"{method} {path} disclosed the read-model marker header.");
            foreach (var id in new[] { clipId, assetId, fileId })
                Assert.DoesNotContain(id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("attachPermitted", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("blockers", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source_ownership", body, StringComparison.OrdinalIgnoreCase);

            output.WriteLine($"{method} {path} -> {(int)response.StatusCode} {response.StatusCode} " +
                             $"[Allow: {(response.Headers.TryGetValues("Allow", out var allowValues) ? string.Join(",", allowValues) : "-")}] body={body.Length}B");
        }

        // No admitted provider work: zero job rows were created ...
        Assert.Equal(0, await CountJobsAsync());
        // ... and the read model's own rows are byte-untouched by the verb storm.
        await AssertSourceRowsIntactAsync(projectId, clipId, assetId, fileId);
    }

    [Fact]
    public async Task Outsider_post_matches_owner_post_so_the_disabled_verb_surface_discloses_nothing_across_identities()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await RegisterAsync(ownerClient);
        var projectId = await CreateProjectAsync(ownerClient, owner, "Cross-identity verb project");
        var (clipId, _, _) = await SeedHealthySourceAsync(ownerClient, owner, projectId);

        using var outsiderClient = factory.CreateClient();
        var outsider = await RegisterAsync(outsiderClient);

        var payload = ProviderWorkPayload(projectId, clipId);
        var ownerResponse = await SendWithCsrfAsync(ownerClient, HttpMethod.Post, ListPath(projectId), payload);
        var outsiderResponse = await SendWithCsrfAsync(outsiderClient, HttpMethod.Post, ListPath(projectId), payload);

        // Same status and byte-identical body: the disabled host cannot be probed
        // for project membership or feature presence through the POST surface.
        Assert.Equal(ownerResponse.StatusCode, outsiderResponse.StatusCode);
        var ownerBody = await ownerResponse.Content.ReadAsStringAsync();
        var outsiderBody = await outsiderResponse.Content.ReadAsStringAsync();
        Assert.Equal(ownerBody, outsiderBody);
        Assert.False(ownerResponse.Headers.Contains("X-Movie-Read-Model"));
        Assert.DoesNotContain(clipId.ToString("D"), ownerBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountJobsAsync());
    }

    [Fact]
    public async Task Anonymous_mutation_attempt_never_becomes_admitted_provider_work()
    {
        using var anonymous = factory.CreateClient();
        var ghostProject = Guid.NewGuid();

        using var request = new HttpRequestMessage(HttpMethod.Post, ListPath(ghostProject));
        request.Content = JsonContent.Create(ProviderWorkPayload(ghostProject, Guid.NewGuid()));
        var response = await anonymous.SendAsync(request); // deliberately no auth, no CSRF
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            (int)response.StatusCode is >= 400 and < 500,
            $"Anonymous POST returned {(int)response.StatusCode}; expected fail-closed 4xx. Body: {body}");
        Assert.False(response.Headers.Contains("X-Movie-Read-Model"));
        Assert.DoesNotContain("attachPermitted", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountJobsAsync());
    }

    [Theory] // Conditional-safe verbs must not return a successful read body on a disabled host.
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Conditional_verbs_never_answer_the_disabled_read_model(string method)
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Conditional verb project");
        var (clipId, _, _) = await SeedHealthySourceAsync(client, owner, projectId);

        foreach (var path in new[] { ListPath(projectId), $"{ListPath(projectId)}/{clipId}" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // A 2xx HEAD/OPTIONS would only be acceptable if completely free of
                // read-model metadata and marker - pin that too, in case routing
                // ever maps these verbs to the GET actions.
                Assert.Empty(body);
                Assert.False(response.Headers.Contains("X-Movie-Read-Model"));
            }
            else
            {
                Assert.True((int)response.StatusCode is >= 400 and < 500, $"{method} {path} returned {(int)response.StatusCode}.");
                Assert.DoesNotContain(clipId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            }

            output.WriteLine($"{method} {path} -> {(int)response.StatusCode} {response.StatusCode} body={body.Length}B");
        }
        Assert.Equal(0, await CountJobsAsync());
    }

    [Fact]
    public async Task Get_remains_opaque_even_with_advisory_request_headers_an_enabled_host_might_honour()
    {
        using var client = factory.CreateClient();
        var owner = await RegisterAsync(client);
        var projectId = await CreateProjectAsync(client, owner, "Header probe project");
        var (clipId, _, _) = await SeedHealthySourceAsync(client, owner, projectId);

        using var request = new HttpRequestMessage(HttpMethod.Get, ListPath(projectId));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Movie-Read-Model", "makeover-source-v1"); // spoofed marker must not unlock anything
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Movie-Read-Model"));
        using var json = JsonDocument.Parse(body);
        Assert.Equal("MOVIE_PROJECT_NOT_FOUND", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(clipId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
    }

    // --- helpers (self-contained; mirrors the authorised fixture contract of #823) ---

    private static string ListPath(Guid projectId) =>
        $"/api/movie-studio/projects/{projectId}/video-makeover-sources";

    private async Task<int> CountJobsAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().GenerationJobs.AsNoTracking().CountAsync();
    }

    private async Task AssertSourceRowsIntactAsync(Guid projectId, Guid clipId, Guid assetId, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.NotNull(await db.MovieClips.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clipId && c.MovieProjectId == projectId));
        Assert.NotNull(await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId));
        Assert.NotNull(await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId));
    }

    private async Task<(Guid ClipId, Guid AssetId, Guid FileId)> SeedHealthySourceAsync(
        HttpClient client, AuthResponse owner, Guid projectId)
    {
        var clipId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var now = DateTime.UtcNow;
        db.StoredFiles.Add(new StoredFile
        {
            Id = fileId,
            WorkspaceId = owner.PersonalWorkspace.Id,
            UserId = owner.User.Id,
            OriginalFileName = "verb-probe.mp4",
            StoredFileName = $"{fileId:N}.mp4",
            Extension = ".mp4",
            ContentType = "video/mp4",
            SizeBytes = 1_500_000,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"tests/makeover-verb-surface/{fileId:N}.mp4",
            Status = StoredFileStatus.Ready,
            CreatedAt = now,
            ProcessedAt = now,
            ContainerFormat = "mp4",
            Width = 1280,
            Height = 720,
            DurationSeconds = 12,
            RetainUntil = now.AddDays(30),
        });
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = owner.PersonalWorkspace.Id,
            CreatedByUserId = owner.User.Id,
            StoredFileId = fileId,
            Name = "Verb probe source",
            AssetType = AssetTypes.Video,
            MimeType = "video/mp4",
            Status = AssetStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MovieClips.Add(new MovieClip
        {
            Id = clipId,
            MovieProjectId = projectId,
            GenerationJobId = null,
            AssetId = assetId,
            StoredFileId = fileId,
            Status = MovieClipStatuses.Ready,
            DurationSeconds = 12,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (clipId, assetId, fileId);
    }

    private async Task<AuthResponse> RegisterAsync(HttpClient client) =>
        await SendWithCsrfAsync<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Makeover Verb Surface QA",
            email = $"makeover-verb-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });

    private async Task<Guid> CreateProjectAsync(HttpClient client, AuthResponse owner, string title)
    {
        var response = await SendWithCsrfAsync<JsonElement>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = owner.PersonalWorkspace.Id,
            mode = "Full",
            title,
            description = "Provider-free verb-surface QA against the default-off makeover gate (#698).",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        return response.GetProperty("project").GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Sends with a valid CSRF token and - unlike the shared helper - returns the
    /// raw response instead of asserting success, because every request this class
    /// makes to the makeover source route is expected to fail closed.
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<T> SendWithCsrfAsync<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
