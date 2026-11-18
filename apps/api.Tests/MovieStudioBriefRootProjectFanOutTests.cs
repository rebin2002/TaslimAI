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
/// The submitted brief description has two persisted targets and the create
/// path's length validation only mirrors the looser one. TaslimDbContext
/// declares MovieProject.Description as character varying(8,000) - the bound
/// MovieStudioValidation applies - while the root Project row that a brand-new
/// Full-mode movie creates declares Project.Description as only character
/// varying(2,000). A description between those two limits therefore clears
/// validation and is then written into a column the model says cannot hold it:
/// the production provider aborts SaveChanges with a varchar length violation
/// (HTTP 500 INTERNAL_ERROR), while the in-memory provider used here does not
/// enforce column sizes and silently stores the over-long root row, which is
/// how these facts see the divergence without touching a provider. The three
/// accompanying facts pin the neighbouring contract any remedy has to keep:
/// the description is only copied into a root project when the create path
/// makes that row itself (a supplied projectId and Quick mode both write the
/// movie row alone, where the wider ceiling legitimately applies), and the
/// value that lands in the root column is the trimmed text, so a brief whose
/// trimmed description sits exactly on the root limit must stay creatable.
/// Sorani and Arabic script are used for the synthetic briefs because that is
/// the product's Kurdish/Arabic path. Every character used is in the Basic
/// Multilingual Plane, so .NET's UTF-16 code-unit count equals the provider's
/// character count and the boundary under test is the fan-out rather than the
/// encoding. Requests are provider-free in-memory HTTP with synthetic text.
/// </summary>
public sealed class MovieStudioBriefRootProjectFanOutTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public MovieStudioBriefRootProjectFanOutTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task BriefCreate_newRootProject_neverStoresADescriptionPastTheRootColumnsDeclaredLimit()
    {
        // One character past Project.Description while still inside the
        // MovieProject.Description ceiling the validator mirrors.
        using var client = factory.CreateClient();
        var auth = await Register(client, "Fan-Out Brief Owner");
        var rootLimit = await DeclaredLimitAsync<Project>(nameof(Project.Description));
        var movieLimit = await DeclaredLimitAsync<MovieProject>(nameof(MovieProject.Description));
        Assert.True(rootLimit < movieLimit,
            $"the root project no longer declares a tighter description column than the movie row ({rootLimit} vs {movieLimit}); the fan-out boundary pinned here has moved, refresh this regression.");
        var description = SoraniText(rootLimit + 1);
        var response = await CreateAsync(client, auth, MovieProjectModes.Full, description);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Fail closed inside the endpoint's own envelope: an accepted remedy.
            Assert.Equal("MOVIE_REQUEST_INVALID", await ReadErrorCode(response));
            return;
        }

        Assert.True(response.IsSuccessStatusCode,
            $"a {description.Length}-character description inside the validated {movieLimit:N0}-character ceiling returned {(int)response.StatusCode} rather than the endpoint's fail-closed 400 envelope: {await response.Content.ReadAsStringAsync()}");
        var created = await ReadCreatedAsync(response);
        Assert.NotNull(created.RootProjectId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var root = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == created.RootProjectId!.Value);
        Assert.True(root.Description!.Length <= rootLimit,
            $"the accepted brief wrote a {root.Description.Length}-character description into Project.Description, which the model declares as character varying({rootLimit}); MovieStudioValidation only bounds the submitted value at the movie column's {movieLimit:N0} characters, so on the production provider this create aborts in SaveChanges with a varchar length violation (500 INTERNAL_ERROR) instead of returning 202.");
    }

    [Fact]
    public async Task BriefCreate_existingRootProject_keepsTheBriefDescriptionOnTheMovieRowAlone()
    {
        // The identical over-root-limit brief is storable when the caller points
        // at a root project the create path does not write: nothing copies the
        // description into that row, so the tight root column is irrelevant and
        // the movie column's own ceiling governs. Pinned so a fix that rejects
        // every long brief outright is visible here.
        using var client = factory.CreateClient();
        var auth = await Register(client, "Existing Root Project Owner");
        var rootLimit = await DeclaredLimitAsync<Project>(nameof(Project.Description));
        var seed = await CreateAsync(client, auth, MovieProjectModes.Full, SoraniText(32));
        Assert.True(seed.IsSuccessStatusCode, await seed.Content.ReadAsStringAsync());
        var seeded = await ReadCreatedAsync(seed);
        Assert.NotNull(seeded.RootProjectId);

        var description = SoraniText(rootLimit + 1);
        var response = await CreateAsync(client, auth, MovieProjectModes.Full, description, seeded.RootProjectId);

        Assert.True(response.IsSuccessStatusCode,
            $"a brief writing no new root Project row was refused although its only description target, MovieProject.Description, holds it: {await response.Content.ReadAsStringAsync()}");
        var created = await ReadCreatedAsync(response);
        Assert.Equal(seeded.RootProjectId, created.RootProjectId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var movie = await db.MovieProjects.AsNoTracking().SingleAsync(item => item.Id == created.MovieProjectId);
        var root = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == seeded.RootProjectId!.Value);
        Assert.Equal(description, movie.Description);
        Assert.Equal(SoraniText(32), root.Description);
    }

    [Fact]
    public async Task BriefCreate_storesTheTrimmedDescriptionInBothTargets_WhenItSitsExactlyOnTheRootLimit()
    {
        // Off-by-one guard on the accepted side: the persisted root description
        // is the trimmed submitted text, so a padded brief whose trimmed value
        // sits exactly on the root column's limit must create and must land on
        // both rows unchanged and inside that limit.
        using var client = factory.CreateClient();
        var auth = await Register(client, "Padded Root Limit Owner");
        var rootLimit = await DeclaredLimitAsync<Project>(nameof(Project.Description));
        var description = ArabicText(rootLimit);
        var response = await CreateAsync(client, auth, MovieProjectModes.Full, $"  {description}  ", language: "ar");

        Assert.True(response.IsSuccessStatusCode,
            $"a description of exactly {rootLimit} trimmed characters fits every persisted target but the create returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var created = await ReadCreatedAsync(response);
        Assert.NotNull(created.RootProjectId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var root = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == created.RootProjectId!.Value);
        var movie = await db.MovieProjects.AsNoTracking().SingleAsync(item => item.Id == created.MovieProjectId);
        Assert.Equal(rootLimit, root.Description!.Length);
        Assert.Equal(description, root.Description);
        Assert.Equal(root.Description, movie.Description);
    }

    [Fact]
    public async Task BriefCreate_quickModeDescriptionWithinTheMovieTarget_isStillAccepted()
    {
        // Scope guard for the fix. The Quick path stores the brief on the movie
        // row alone, so its only description target is the wider column and a
        // Sorani brief on that ceiling is storable as it stands. Tightening the
        // submitted description to the root column everywhere would start
        // refusing briefs this flow can persist, so the root-column bound has
        // to follow the write that actually makes the root row.
        using var client = factory.CreateClient();
        var auth = await Register(client, "Quick Mode Ceiling Owner");
        var movieLimit = await DeclaredLimitAsync<MovieProject>(nameof(MovieProject.Description));
        var description = SoraniText(movieLimit);
        var response = await CreateAsync(client, auth, MovieProjectModes.Quick, description);

        Assert.True(response.IsSuccessStatusCode,
            $"Quick mode writes no root Project row, so a {description.Length}-character brief fits its only target ({movieLimit:N0} characters) but the create returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var created = await ReadCreatedAsync(response);
        Assert.Null(created.RootProjectId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var movie = await db.MovieProjects.AsNoTracking().SingleAsync(item => item.Id == created.MovieProjectId);
        Assert.Equal(description, movie.Description);
    }

    private static string SoraniText(int length) => new('ک', length);

    private static string ArabicText(int length) => new('ش', length);

    private async Task<HttpResponseMessage> CreateAsync(HttpClient client, AuthResponse auth, string mode, string description, Guid? rootProjectId = null, string language = "ku") =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new Dictionary<string, object?>
        {
            ["workspaceId"] = auth.PersonalWorkspace.Id,
            ["mode"] = mode,
            ["projectId"] = rootProjectId,
            ["title"] = "Fan-Out Boundary Brief",
            ["description"] = description,
            ["durationSeconds"] = 60,
            ["aspectRatio"] = "16:9",
            ["style"] = "cinematic",
            ["language"] = language,
        });

    private static async Task<CreatedBrief> ReadCreatedAsync(HttpResponseMessage response)
    {
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var project = created.GetProperty("project");
        var movieProjectId = project.GetProperty("id").GetGuid();
        var rootProject = project.GetProperty("projectId");
        return new CreatedBrief(movieProjectId, rootProject.ValueKind == JsonValueKind.Null ? null : rootProject.GetGuid());
    }

    private sealed record CreatedBrief(Guid MovieProjectId, Guid? RootProjectId);

    private async Task<int> DeclaredLimitAsync<TEntity>(string propertyName) where TEntity : class
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return db.Model.FindEntityType(typeof(TEntity))!.FindProperty(propertyName)!.GetMaxLength()!.Value;
    }

    private static async Task<string?> ReadErrorCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString()
            : null;

    private static async Task<AuthResponse> Register(HttpClient client, string displayName) =>
        await SendWithCsrf<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"fanout-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
