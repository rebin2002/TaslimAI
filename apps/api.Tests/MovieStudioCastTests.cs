using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieStudioCastTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public MovieStudioCastTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Character_card_states_relationships_and_locks_are_persisted_and_returned()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Cast Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);

        var character = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new
        {
            name = "Mara",
            role = "Lead investigator",
            description = "A methodical investigator who notices what others miss.",
            appearance = "Dark curls and a small silver scar under the left eye.",
            physicalDescription = "Tall, athletic build.",
            wardrobe = "Charcoal coat and practical boots.",
            voiceReference = "Low, measured, warm contralto.",
            personalityAndStoryNotes = "Protective, observant, and unwilling to abandon a witness.",
            voiceAndPerformance = "Understated delivery; restrained urgency under pressure.",
            continuityNotes = "Silver scar remains visible in close shots."
        });

        Assert.Equal("Lead investigator", character.Role);
        Assert.Equal("Charcoal coat and practical boots.", character.Wardrobe);
        Assert.Empty(character.ReferenceAssetIds);

        var state = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterStateDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{character.Id}/states", new
        {
            key = "after-storm",
            label = "After the storm",
            wardrobe = "Soaked charcoal coat with a torn right sleeve.",
            ageOrTimeState = "Same night, two hours later",
            appearance = "Mud on the cheek; silver scar still visible.",
            injuryOrCondition = "Shallow cut on the right palm.",
            locationOrStoryState = "At the abandoned ferry terminal.",
            continuityNotes = "Keeps the evidence envelope in her left hand."
        });

        var second = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new
        {
            name = "Ilan",
            role = "Witness",
            description = "A guarded dock worker who knows the missing route."
        });
        var relationship = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterRelationshipDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{character.Id}/relationships", new
        {
            relatedCharacterId = second.Id,
            relationshipType = "protects",
            notes = "Mara promised to keep Ilan out of the case file."
        });
        Assert.Equal(second.Id, relationship.RelatedCharacterId);
        Assert.Equal("protects", relationship.RelationshipType);

        var cardLock = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterContinuityLockDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{character.Id}/continuity-locks", new
        {
            fieldKey = "wardrobe",
            lockedValue = "Charcoal coat and practical boots."
        });
        Assert.Equal("wardrobe", cardLock.FieldKey);
        Assert.Null(cardLock.CharacterStateId);

        var stateLock = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterContinuityLockDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{character.Id}/continuity-locks", new
        {
            characterStateId = state.Id,
            fieldKey = "injuryOrCondition",
            lockedValue = "Shallow cut on the right palm."
        });
        Assert.Equal(state.Id, stateLock.CharacterStateId);

        var project = await client.GetFromJsonAsync<Taslim.Api.Movies.MovieStudioProjectDto>($"/api/movie-studio/projects/{movie.Project.Id}");
        var persisted = Assert.Single(project!.Characters, item => item.Id == character.Id);
        Assert.Equal("Lead investigator", persisted.Role);
        Assert.Equal("After the storm", Assert.Single(persisted.States).Label);
        Assert.Equal(second.Id, Assert.Single(persisted.Relationships).RelatedCharacterId);
        Assert.Equal(2, persisted.ContinuityLocks.Count);
    }

    [Fact]
    public async Task Approved_card_and_state_facts_cannot_be_changed_and_are_included_in_generation_snapshot()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Continuity Owner");
        var movie = await CreateMovie(client, auth.PersonalWorkspace.Id);
        var character = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new
        {
            name = "Locked Mara",
            description = "A continuity test character.",
            appearance = "Short dark hair",
            wardrobe = "Blue field jacket"
        });
        var state = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterStateDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{character.Id}/states", new
        {
            key = "injured",
            injuryOrCondition = "Bruised shoulder"
        });
        await SendWithCsrf<Taslim.Api.Movies.MovieCharacterContinuityLockDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{character.Id}/continuity-locks", new { fieldKey = "appearance", lockedValue = "Short dark hair" });
        await SendWithCsrf<Taslim.Api.Movies.MovieCharacterContinuityLockDto>(client, HttpMethod.Post, $"/api/movie-studio/characters/{character.Id}/continuity-locks", new { characterStateId = state.Id, fieldKey = "injuryOrCondition", lockedValue = "Bruised shoulder" });

        var blockedCard = await SendWithCsrf(client, HttpMethod.Patch, $"/api/movie-studio/characters/{character.Id}", new { name = "Locked Mara", description = "A continuity test character.", appearance = "Long blonde hair", wardrobe = "Blue field jacket" });
        Assert.Equal(HttpStatusCode.Conflict, blockedCard.StatusCode);
        Assert.Equal("MOVIE_CHARACTER_CONTINUITY_LOCKED", (await blockedCard.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        var blockedState = await SendWithCsrf(client, HttpMethod.Patch, $"/api/movie-studio/character-states/{state.Id}", new { key = "injured", injuryOrCondition = "Broken arm" });
        Assert.Equal(HttpStatusCode.Conflict, blockedState.StatusCode);

        var scene = await SendWithCsrf<Taslim.Api.Movies.MovieSceneDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes", new { title = "Ferry", summary = "Mara reaches the ferry terminal." });
        var generation = await SendWithCsrf<Taslim.Api.Movies.MovieStudioGenerationResponse>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/scenes/{scene.Id}/generate", new { title = "Continuity snapshot" });
        var generatedClip = generation.Project.Scenes.SelectMany(item => item.Clips).Single(item => item.Id == generation.ClipId);
        Assert.Contains("Locked Mara", generatedClip.ContinuitySnapshotJson);
        Assert.Contains("Short dark hair", generatedClip.ContinuitySnapshotJson);
        Assert.Contains("Bruised shoulder", generatedClip.ContinuitySnapshotJson);
    }

    [Fact]
    public async Task Cast_apis_are_workspace_isolated()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Private Cast Owner");
        var movie = await CreateMovie(owner, auth.PersonalWorkspace.Id);
        var character = await SendWithCsrf<Taslim.Api.Movies.MovieCharacterDto>(owner, HttpMethod.Post, $"/api/movie-studio/projects/{movie.Project.Id}/characters", new { name = "Private Character", description = "Private." });

        using var other = factory.CreateClient();
        await Register(other, "Other Cast User");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/movie-studio/projects/{movie.Project.Id}")).StatusCode);
        var update = await SendWithCsrf(other, HttpMethod.Patch, $"/api/movie-studio/characters/{character.Id}", new { name = "Stolen", description = "No." });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    private static async Task<Taslim.Api.Movies.MovieStudioProjectResponse> CreateMovie(HttpClient client, Guid workspaceId) =>
        await SendWithCsrf<Taslim.Api.Movies.MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId,
            mode = "Full",
            title = "Cast Continuity",
            description = "A movie for cast continuity tests.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en"
        });

    private static async Task<AuthResponse> Register(HttpClient client, string displayName) =>
        await SendWithCsrf<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"cast-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
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
