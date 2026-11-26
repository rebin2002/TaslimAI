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
/// MovieProject.Description accepts 8,000 characters, but a new Full-mode
/// create also writes to Project.Description, whose column accepts 2,000.
/// Validation must reject 2,001 before persistence with MOVIE_REQUEST_INVALID
/// and leave neither root nor movie records behind. A supplied projectId and
/// Quick mode write the movie row alone, where 8,000 remains valid; a new Full
/// project accepts a trimmed description exactly at 2,000. The synthetic HTTP
/// requests use the in-memory test factory and never contact a provider.
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
        // One character past Project.Description but still inside the movie
        // column's wider ceiling; refusal must happen before either row exists.
        using var client = factory.CreateClient();
        var auth = await Register(client, "Fan-Out Brief Owner");
        var rootLimit = await DeclaredLimitAsync<Project>(nameof(Project.Description));
        var movieLimit = await DeclaredLimitAsync<MovieProject>(nameof(MovieProject.Description));
        Assert.Equal(2_000, rootLimit);
        Assert.Equal(8_000, movieLimit);
        Assert.True(rootLimit < movieLimit,
            $"the root project no longer declares a tighter description column than the movie row ({rootLimit} vs {movieLimit}); the fan-out boundary pinned here has moved, refresh this regression.");
        var description = SoraniText(rootLimit + 1);
        var title = $"Rejected Fan-Out Brief {Guid.NewGuid():N}";
        var response = await CreateAsync(client, auth, MovieProjectModes.Full, description, title: title);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_REQUEST_INVALID", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains($"{rootLimit:N0}", error.GetProperty("error").GetProperty("message").GetString() ?? string.Empty);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Projects.AsNoTracking().AnyAsync(item => item.Name == title && item.Type == ProjectTypes.Movie));
        Assert.False(await db.MovieProjects.AsNoTracking().AnyAsync(item => item.Title == title));
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

    private async Task<HttpResponseMessage> CreateAsync(HttpClient client, AuthResponse auth, string mode, string description, Guid? rootProjectId = null, string language = "ku", string? title = null) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new Dictionary<string, object?>
        {
            ["workspaceId"] = auth.PersonalWorkspace.Id,
            ["mode"] = mode,
            ["projectId"] = rootProjectId,
            ["title"] = title ?? "Fan-Out Boundary Brief",
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
