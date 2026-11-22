using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Regression coverage for the Social Studio job-title boundary: the controller
// derives the job title from the first 160 characters of the prompt. A naive
// cut can split a surrogate pair (e.g. an emoji landing on the boundary),
// persisting a lone surrogate in the job title and leaking U+FFFD replacement
// characters into every serialized response. Titles must always stay well
// formed, exactly like the canonical document-contract truncation gate.
public sealed class SocialJobTitleTruncationTests : IClassFixture<SocialGenerationApiFactory>
{
    private readonly SocialGenerationApiFactory factory;

    public SocialJobTitleTruncationTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Job_title_never_splits_surrogate_pair_at_truncation_boundary()
    {
        var (client, workspaceId) = await AuthenticatedClient();
        var prefix = new string('a', 159);
        // 166 UTF-16 code units; the rocket emoji occupies indices 159-160, so the
        // 160-character cut lands in the middle of the pair.
        var jobId = await CreateSocialJob(client, workspaceId, prefix + "\uD83D\uDE80 tail");

        var title = await ReadPersistedTitleAsync(jobId);

        Assert.NotNull(title);
        Assert.False(title.Length > 0 && char.IsHighSurrogate(title[^1]) && !char.IsLowSurrogate(title[^1]), "The persisted title ends with a dangling high surrogate.");
        Assert.Equal(prefix, title);
    }

    [Fact]
    public async Task Job_title_keeps_complete_emoji_pairs_up_to_the_boundary()
    {
        var (client, workspaceId) = await AuthenticatedClient();
        // Three rockets start at index 158, so index 160 is a safe pair boundary.
        var jobId = await CreateSocialJob(client, workspaceId, new string('b', 158) + "\uD83D\uDE80\uD83D\uDE80\uD83D\uDE80 tail");

        var title = await ReadPersistedTitleAsync(jobId);

        Assert.Equal(new string('b', 158) + "\uD83D\uDE80", title);
    }

    [Fact]
    public async Task Job_title_is_preserved_verbatim_below_the_boundary()
    {
        var (client, workspaceId) = await AuthenticatedClient();
        var prompt = "Announce 🚀 the launch — no truncation needed here.";
        var jobId = await CreateSocialJob(client, workspaceId, prompt);

        var title = await ReadPersistedTitleAsync(jobId);

        Assert.Equal(prompt, title);
    }

    private async Task<(HttpClient, Guid)> AuthenticatedClient()
    {
        var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        Guid workspaceId;
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register"))
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { displayName = "Title Boundary Tester", email = $"social-title-{Guid.NewGuid():N}@example.com", password = "StrongPassword!1", preferredLanguage = "en" }),
                Encoding.UTF8, "application/json");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
            workspaceId = auth.PersonalWorkspace.Id;
        }
        return (client, workspaceId);
    }

    private async Task<Guid> CreateSocialJob(HttpClient client, Guid workspaceId, string prompt)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/social-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                workspaceId,
                prompt,
                socialType = "announcement",
                platform = "linkedin",
                tone = "professional",
                language = "en",
            }),
            Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        return created.Job.Id;
    }

    private async Task<string?> ReadPersistedTitleAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == jobId);
        return job?.Title;
    }
}
