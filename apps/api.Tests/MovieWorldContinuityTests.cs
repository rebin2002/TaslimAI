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

public sealed class MovieWorldContinuityTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieWorldContinuityTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void Conflicts_are_deterministic_and_explain_source_and_target()
    {
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var locationA = Guid.NewGuid();
        var locationB = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var propId = Guid.NewGuid();
        var variationA = Guid.NewGuid();
        var variationB = Guid.NewGuid();
        var setUsageId = Guid.NewGuid();
        var locationUsageId = Guid.NewGuid();
        var factId = Guid.NewGuid();
        var lockId = Guid.NewGuid();
        var target = new MovieWorldContinuityTarget(MovieWorldScopes.Shot, projectId, sceneId, shotId);
        var usages = new[]
        {
            new MovieWorldUsage { Id = setUsageId, MovieProjectId = projectId, MovieSceneId = sceneId, MovieShotId = shotId, EntityType = MovieWorldEntityTypes.Set, EntityId = setId, Role = $"variationId={variationA}" },
            new MovieWorldUsage { Id = locationUsageId, MovieProjectId = projectId, MovieSceneId = sceneId, MovieShotId = shotId, EntityType = MovieWorldEntityTypes.Location, EntityId = locationB },
        };
        var locations = new[]
        {
            new MovieWorldContinuityLocationSnapshot(locationA, "Location A", "A", null, null),
            new MovieWorldContinuityLocationSnapshot(locationB, "Location B", "B", null, null),
        };
        var sets = new[]
        {
            new MovieWorldContinuitySetSnapshot(setId, locationA, "Set", "S", "practical", null, null, null, null, null,
            [
                new MovieWorldContinuityVariationSnapshot(variationA, setId, "Day", null, "day", null, null, null, null, false),
                new MovieWorldContinuityVariationSnapshot(variationB, setId, "Night", null, "night", null, null, null, null, false),
            ]),
        };
        var props = new[] { new MovieWorldContinuityPropSnapshot(propId, "Prop", "P", null, null, null, null) };
        var facts = new[] { new MovieWorldContinuityFactSnapshot(factId, MovieWorldScopes.Prop, propId, "state", "open", null, DateTime.UtcNow) };
        var locks = new[]
        {
            new MovieWorldContinuityLockSnapshot(lockId, MovieWorldEntityTypes.Prop, propId, "state", "closed", "hard", "approved state", DateTime.UtcNow),
            new MovieWorldContinuityLockSnapshot(Guid.NewGuid(), MovieWorldEntityTypes.Set, setId, "variationId", variationB.ToString(), "hard", null, DateTime.UtcNow),
        };

        var warnings = MovieWorldContinuityConflictDetector.Detect(target, usages, locations, sets, props, facts, locks);

        Assert.Equal(["conflicting_location_set_usage", "locked_variation_mismatch", "prop_state_inconsistency"], warnings.Select(item => item.Code).OrderBy(item => item).ToArray());
        var propWarning = Assert.Single(warnings, item => item.Code == "prop_state_inconsistency");
        Assert.True(propWarning.Source.RecordId is Guid recordId && (recordId == factId || recordId == lockId));
        Assert.Equal(shotId, propWarning.Target.ShotId);
        Assert.Equal(projectId, propWarning.Target.MovieProjectId);
        Assert.Contains("closed", propWarning.Message);
        Assert.Contains("open", propWarning.Message);
    }

    [Fact]
    public async Task Shot_projection_is_bounded_to_target_usage_and_workspace_isolation_is_preserved()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Continuity Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        var usedLocation = await SendJson<MovieLocationDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Used Location", description = "Relevant." });
        var unusedLocation = await SendJson<MovieLocationDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Unused Location", description = "Not relevant." });
        var set = await SendJson<MovieSetDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/sets", new { name = "Used Set", description = "Relevant set.", movieLocationId = usedLocation.Id });
        var prop = await SendJson<MoviePropDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/props", new { name = "Used Prop", description = "Relevant prop." });
        var scene = await SendJson<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Continuity Scene", summary = "A bounded target." });
        var shot = await SendJson<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "Target shot." });
        await SendJson<MovieWorldUsageDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new { entityType = "set", entityId = set.Id });
        await SendJson<MovieWorldUsageDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new { entityType = "prop", entityId = prop.Id, movieShotId = shot.Id });

        var response = await client.GetAsync($"/api/movie-studio/shots/{shot.Id}/world-continuity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = (await response.Content.ReadFromJsonAsync<MovieWorldContinuitySnapshotDto>())!;
        Assert.Equal(movie.Project.Id, snapshot.MovieProjectId);
        Assert.Equal(scene.Id, snapshot.SceneId);
        Assert.Equal(shot.Id, snapshot.ShotId);
        Assert.Equal(1, snapshot.SnapshotVersion);
        Assert.Contains(snapshot.Locations, item => item.Id == usedLocation.Id);
        Assert.DoesNotContain(snapshot.Locations, item => item.Id == unusedLocation.Id);
        Assert.Single(snapshot.Sets);
        Assert.Single(snapshot.Props);
        Assert.NotEmpty(snapshot.SnapshotHash);
        var repeated = (await (await client.GetAsync($"/api/movie-studio/shots/{shot.Id}/world-continuity")).Content.ReadFromJsonAsync<MovieWorldContinuitySnapshotDto>())!;
        Assert.Equal(snapshot.SnapshotHash, repeated.SnapshotHash);
        Assert.Equal(snapshot.SnapshotVersion, repeated.SnapshotVersion);

        using var outsider = factory.CreateClient();
        await Register(outsider, "Other Continuity User");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/movie-studio/shots/{shot.Id}/world-continuity")).StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"movie-continuity-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new { workspaceId, mode = "Full", title = "Bounded Continuity", description = "World continuity projection.", durationSeconds = 30, aspectRatio = "16:9", style = "cinematic", language = "en" });
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
