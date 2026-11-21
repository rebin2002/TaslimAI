using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The Movie Studio brief create contract must fail closed with 400
/// MOVIE_REQUEST_INVALID whenever a brief field carries a value the persisted
/// model declares it cannot store (TaslimDbContext: Style &lt;= 100,
/// AdditionalInstructions &lt;= 8,000, guide direction fields &lt;= 4,000,
/// ContinuityRules &lt;= 8,000). Before this regression the request validator
/// skipped these fields, so an explicit null style crashed the create path
/// with a 500 INTERNAL_ERROR and oversized direction text either escaped the
/// endpoint's own validation envelope or violated the production column
/// limits at save time.
/// </summary>
public sealed class MovieStudioBriefValidationTests : IClassFixture<GenerationJobsApiFactory>
{
    private const int StyleMax = 100;
    private const int AdditionalInstructionsMax = 8_000;
    private const int DirectionFieldMax = 4_000;
    private const int ContinuityRulesMax = 8_000;
    private readonly GenerationJobsApiFactory factory;

    public MovieStudioBriefValidationTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task BriefCreate_returnsBadRequest_WhenStyleIsExplicitlyNull()
    {
        // Guard: the create path must fail closed inside its 400 validation
        // envelope when a client sends an explicit null for the persisted,
        // non-nullable Style field, never a 500.
        using var client = factory.CreateClient();
        var auth = await Register(client, "Brief Null Style Owner");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Null Style",
            description = "A brief that must never crash the create path.",
            durationSeconds = 60,
            aspectRatio = "16:9",
            style = (string?)null,
            language = "en",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCode(response));
    }

    [Fact]
    public async Task BriefCreate_failsClosed_WhenBriefFieldsExceedPersistedLimits()
    {
        // Every bounded brief field must be refused with 400
        // MOVIE_REQUEST_INVALID once it exceeds the limit the EF model
        // declares for its column; on the production provider an unbounded
        // value instead aborts SaveChanges with a 500 INTERNAL_ERROR.
        using var client = factory.CreateClient();
        var auth = await Register(client, "Brief Oversize Owner");

        var oversized = new[]
        {
            ("style", new string('s', StyleMax + 1)),
            ("additionalInstructions", new string('a', AdditionalInstructionsMax + 1)),
            ("visualLanguage", new string('v', DirectionFieldMax + 1)),
            ("cameraLanguage", new string('c', DirectionFieldMax + 1)),
            ("colorAndLighting", new string('l', DirectionFieldMax + 1)),
            ("soundAndNarration", new string('n', DirectionFieldMax + 1)),
            ("continuityRules", new string('r', ContinuityRulesMax + 1)),
        };

        foreach (var (field, value) in oversized)
        {
            var payload = new Dictionary<string, object?>
            {
                ["workspaceId"] = auth.PersonalWorkspace.Id,
                ["mode"] = MovieProjectModes.Full,
                ["title"] = $"Oversize {field}",
                ["description"] = "A brief whose creative direction exceeds the model limits.",
                ["durationSeconds"] = 60,
                ["aspectRatio"] = "16:9",
                ["style"] = "cinematic",
                ["language"] = "en",
                [field] = value,
            };
            var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", payload);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
                $"{field} of {value.Length} characters returned {(int)response.StatusCode} instead of 400.");
            Assert.Equal("MOVIE_REQUEST_INVALID", await ReadErrorCode(response));
        }
    }

    [Fact]
    public async Task BriefCreate_succeeds_WhenDirectionFieldsAreExactlyAtPersistedLimits()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Brief Boundary Owner");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = "Boundary Brief",
            description = "Every creative direction field sits exactly on its persisted limit.",
            durationSeconds = 60,
            aspectRatio = "16:9",
            style = new string('s', StyleMax),
            language = "en",
            additionalInstructions = new string('a', AdditionalInstructionsMax),
            visualLanguage = new string('v', DirectionFieldMax),
            cameraLanguage = new string('c', DirectionFieldMax),
            colorAndLighting = new string('l', DirectionFieldMax),
            soundAndNarration = new string('n', DirectionFieldMax),
            continuityRules = new string('r', ContinuityRulesMax),
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> ReadErrorCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString()
            : null;

    private static async Task<AuthResponse> Register(HttpClient client, string displayName) =>
        await SendWithCsrf<AuthResponse>(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"brief-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });

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
