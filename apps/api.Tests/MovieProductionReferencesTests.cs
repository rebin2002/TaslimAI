using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieProductionReferencesTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieProductionReferencesTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Package_uses_locked_guide_precedence_and_keeps_all_reference_sections_bounded()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Reference Package Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Legacy visual language");
        var revision = await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/revisions", new
        {
            visualBibleJson = "{\"visualLanguage\":\"Locked visual language\",\"colorAndLighting\":\"Locked amber light\"}",
            cinematographyBibleJson = "{\"cameraLanguage\":\"Locked restrained camera\"}",
            continuityBibleJson = "{\"continuityRules\":\"Locked red notebook\"}",
            storyBibleJson = "{}",
            characterBibleReferencesJson = "[]",
            worldBibleReferencesJson = "[]",
            audioBibleJson = "{}",
        });
        var locked = await SendWithCsrf<MovieGuideRevisionResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/guide/lock", new { revisionNumber = revision.Revision.RevisionNumber });
        Assert.True(locked.AuthoritativeContext!.IsAuthoritative);

        var character = await SendWithCsrf<MovieCharacterDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new
        {
            name = "Mara", role = "Lead", description = "A careful investigator.", appearance = "Short dark hair", wardrobe = "Charcoal coat",
        });
        await SendWithCsrf<MovieCharacterStateDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{character.Id}/states", new
        {
            key = "injured", label = "Injured", wardrobe = "Green scarf", appearance = "Bandaged hand",
        });
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new
        {
            title = "Ferry", summary = "Mara waits at the ferry terminal.", durationSeconds = 10,
        });
        var shot = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new
        {
            description = "Mara checks the red notebook while injured.", subjectCharacterIds = new[] { character.Id }, cameraAndFraming = "Medium close-up",
        });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            db.MovieProductionVersions.Add(new MovieProductionVersion
            {
                Id = Guid.NewGuid(), MovieShotId = shot.Id, VersionNumber = 1, Stage = MovieProductionStages.ApprovedKeyframe,
                Status = MovieProductionVersionStatuses.Approved, Label = "Approved keyframe", CompositionJson = "{\"frame\":\"locked\"}",
                CreatedByUserId = auth.User.Id, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        var location = await SendWithCsrf<MovieLocationDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/locations", new { name = "Ferry terminal", description = "A wet concrete platform." });
        var prop = await SendWithCsrf<MoviePropDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/props", new { name = "Red notebook", description = "A weathered notebook.", category = "evidence" });
        await SendWithCsrf<MovieWorldUsageDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new { entityType = "location", entityId = location.Id, movieShotId = shot.Id });
        await SendWithCsrf<MovieWorldUsageDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/world-usage", new { entityType = "prop", entityId = prop.Id, movieShotId = shot.Id });

        var response = await client.GetAsync($"/api/movie-studio/shots/{shot.Id}/production-references");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var package = (await response.Content.ReadFromJsonAsync<MovieProductionReferencePackageDto>())!;
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(movie.Project.Id, package.MovieProjectId);
        Assert.Equal(shot.Id, package.MovieShotId);
        Assert.True(package.Guide.IsLocked);
        Assert.Equal(revision.Revision.RevisionNumber, package.Guide.RevisionNumber);
        Assert.Equal("Locked visual language", package.Style.VisualLanguage);
        Assert.Equal("Locked restrained camera", package.Style.CameraLanguage);
        Assert.Contains(package.Characters, item => item.CharacterId == character.Id && item.StateWardrobe == "Green scarf");
        Assert.Contains(package.Wardrobe, item => item.CharacterId == character.Id && item.Wardrobe == "Green scarf");
        Assert.Contains(package.Locations, item => item.LocationId == location.Id);
        Assert.Contains(package.Props, item => item.PropId == prop.Id);
        Assert.NotNull(package.Keyframe);
        Assert.Equal(MovieProductionStages.ApprovedKeyframe, package.Keyframe!.Stage);
        Assert.Equal(64, package.Keyframe.ContentHash.Length);
        Assert.Equal(64, package.PackageHash.Length);
        Assert.All(package.Characters, item => Assert.Equal(64, item.Provenance.SourceHash.Length));
        Assert.True(json.Length <= MovieProductionReferenceLimits.MaxPackageJsonLength);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);

        using var outsider = factory.CreateClient();
        await Register(outsider, "Reference Package Outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/movie-studio/shots/{shot.Id}/production-references")).StatusCode);
    }

    [Fact]
    public async Task Package_hash_is_stable_and_contains_the_immediate_previous_shot_provenance()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Previous Shot Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id, "Style");
        var scene = await SendWithCsrf<MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new
        {
            title = "Platform", summary = "A simple continuity test scene.", durationSeconds = 12,
        });
        var first = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "The first shot." });
        var second = await SendWithCsrf<MovieShotDto>(client, HttpMethod.Post, $"/api/movie-studio/scenes/{scene.Id}/shots", new { description = "The second shot." });

        var firstResponse = await client.GetAsync($"/api/movie-studio/shots/{second.Id}/production-references");
        var firstPackage = (await firstResponse.Content.ReadFromJsonAsync<MovieProductionReferencePackageDto>())!;
        var secondResponse = await client.GetAsync($"/api/movie-studio/shots/{second.Id}/production-references");
        var secondPackage = (await secondResponse.Content.ReadFromJsonAsync<MovieProductionReferencePackageDto>())!;

        var previous = Assert.Single(firstPackage.PreviousShots);
        Assert.Equal(first.Id, previous.ShotId);
        Assert.Equal("The first shot.", previous.Description);
        Assert.Equal(64, previous.ContentHash.Length);
        Assert.Equal(firstPackage.PackageHash, secondPackage.PackageHash);
        Assert.Equal(firstPackage.World.SnapshotHash, secondPackage.World.SnapshotHash);
        Assert.Contains(firstPackage.Warnings, item => item.Code == "guide_not_locked");
        Assert.Contains(firstPackage.Warnings, item => item.Code == "approved_keyframe_missing");
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"movie-references-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId, string visualLanguage)
    {
        return await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId,
            mode = MovieProjectModes.Full,
            title = "Reference Package",
            description = "A bounded production reference test movie.",
            durationSeconds = 30,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
            visualLanguage,
            cameraLanguage = "Legacy camera",
            colorAndLighting = "Legacy light",
            continuityRules = "Legacy rules",
        });
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
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
