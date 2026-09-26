using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieWorldTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieWorldTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task World_records_are_reusable_across_scene_and_shot_and_return_in_project_contract()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "World Contract Movie");
        var location = await SendJson<MovieLocationDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Old Harbor", description = "A repeatable waterfront location.", visualContinuityNotes = "Rust-red cranes stay visible on the east horizon." });
        var set = await SendJson<MovieSetDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/sets", new { name = "Harbor Warehouse", description = "Interior practical warehouse set.", environmentType = "practical", movieLocationId = location.Id, timeOfDay = "blue hour", weather = "light rain" });
        var variation = await SendJson<MovieSetVariationDto>(client, HttpMethod.Post, $"/api/movie-studio/sets/{set.Id}/variations", new { name = "Storm night", timeOfDay = "night", weather = "heavy rain", lighting = "cold moonlight and sodium spill", isDefault = true });
        var prop = await SendJson<MoviePropDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/props", new { name = "Brass Compass", description = "A worn brass compass with a cracked glass face.", category = "hero prop" });
        var reference = await SendJson<MovieWorldReferenceDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/world-references", new { name = "Harbor palette", kind = "moodboard", description = "Muted teal, rust, and sodium amber.", tagsJson = "[\"harbor\",\"palette\"]" });
        var scene = await SendJson<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "The Arrival", summary = "The courier enters the warehouse.", durationSeconds = 8 });
        var shot = await SendJson<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "A slow push toward the compass on a crate." });
        var sceneUsage = await SendJson<MovieWorldUsageDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new { entityType = "set", entityId = set.Id, role = "primary environment" });
        var shotUsage = await SendJson<MovieWorldUsageDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new { entityType = "prop", entityId = prop.Id, movieShotId = shot.Id, role = "hero prop" });
        var fact = await SendJson<MovieContinuityFactDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/continuity-facts", new { scopeType = "scene", scopeId = scene.Id, factKey = "compass_bearing", factValue = "north-east", notes = "Must match the warehouse window direction." });
        var lockRecord = await SendJson<MovieContinuityLockDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/continuity-locks", new { entityType = "prop", entityId = prop.Id, fieldName = "glass_damage", lockedValue = "single crack across upper-left glass", strength = "hard" });
        var generation = await SendJson<MovieStudioGenerationResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes/{scene.Id}/generate", new { title = "World-aware scene clip" });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == generation.Job.Id);
            using var input = JsonDocument.Parse(job.InputJson);
            var worldContext = input.RootElement.GetProperty("WorldContextJson").GetString();
            Assert.Contains(location.Id.ToString(), worldContext);
            Assert.Contains(set.Id.ToString(), worldContext);
            Assert.Contains(prop.Id.ToString(), worldContext);
        }

        var loaded = await client.GetFromJsonAsync<MovieStudioProjectDto>($"/api/movie-studio/projects/{movie.Project.Id}");
        Assert.NotNull(loaded);
        Assert.Contains(loaded!.World.Locations, item => item.Id == location.Id);
        Assert.Contains(loaded.World.Sets, item => item.Id == set.Id && item.MovieLocationId == location.Id && item.Variations.Any(state => state.Id == variation.Id && state.IsDefault));
        Assert.Contains(loaded.World.Props, item => item.Id == prop.Id);
        Assert.Contains(loaded.World.References, item => item.Id == reference.Id);
        Assert.Contains(loaded.World.Usages, item => item.Id == sceneUsage.Id && item.MovieShotId is null);
        Assert.Contains(loaded.World.Usages, item => item.Id == shotUsage.Id && item.MovieShotId == shot.Id);
        Assert.Contains(loaded.World.Facts, item => item.Id == fact.Id && item.ScopeId == scene.Id);
        Assert.Contains(loaded.World.Locks, item => item.Id == lockRecord.Id && item.Strength == "hard");
        Assert.Single(loaded.World.Sets);
        Assert.Single(loaded.World.Props);
    }

    [Fact]
    public async Task Movie_world_is_workspace_isolated_and_invalid_cross_project_usage_is_rejected()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Private World Owner");
        var movie = await CreateMovie(owner, auth.PersonalWorkspace.Id, "Private World Movie");
        var location = await SendJson<MovieLocationDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Private Street", description = "A private location." });
        var scene = await SendJson<MovieSceneDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Private Scene", summary = "Private scene summary." });

        using var other = factory.CreateClient();
        await Register(other, "Other World User");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/movie-studio/projects/{movie.Project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrf(other, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/sets", new { name = "Intruder Set", description = "Should not persist.", movieLocationId = location.Id })).StatusCode);

        var invalidUsage = await SendWithCsrf(owner, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new { entityType = "set", entityId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, invalidUsage.StatusCode);
        var error = await invalidUsage.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_WORLD_USAGE_INVALID", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task World_read_model_is_scoped_and_locked_identity_cannot_be_overwritten()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "World Lock Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "World Lock Movie");
        var location = await SendJson<MovieLocationDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Locked Pier", description = "The pier has one continuous geography.", visualContinuityNotes = "Keep the lighthouse on frame right." });

        var world = await client.GetFromJsonAsync<JsonElement>($"/api/movie-studio/projects/{movie.Project.Id}/world");
        Assert.Equal(movie.Project.Id, world.GetProperty("movieProjectId").GetGuid());
        Assert.Equal("World Lock Movie", world.GetProperty("title").GetString());
        Assert.Contains(world.GetProperty("world").GetProperty("locations").EnumerateArray(), item => item.GetProperty("id").GetGuid() == location.Id);
        Assert.False(world.GetProperty("world").TryGetProperty("scenes", out _));

        var lockRecord = await SendJson<MovieContinuityLockDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/continuity-locks", new { entityType = "location", entityId = location.Id, fieldName = "description", lockedValue = location.Description, strength = "hard" });
        Assert.Equal("hard", lockRecord.Strength);
        var rejected = await SendWithCsrf(client, HttpMethod.Patch, $"/api/movie-studio/locations/{location.Id}", new { name = location.Name, description = "A changed geography that must not replace the lock.", visualContinuityNotes = location.VisualContinuityNotes, referenceAssetId = location.ReferenceAssetId });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MOVIE_WORLD_CONTINUITY_LOCKED", error.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"movie-world-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId, string title)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new { workspaceId, mode = "Full", title, description = "A durable world foundation test movie.", durationSeconds = 30, aspectRatio = "16:9", style = "cinematic", language = "en" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MovieStudioProjectResponse>())!;
    }

    private static async Task<T> SendJson<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
