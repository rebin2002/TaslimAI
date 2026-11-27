using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Pins the canonical Social job-result payload contract.
///
/// The published result is consumed by the Social Studio state machine, which reads
/// <c>job.resultJson</c> with camelCase members (<c>socialType</c>, <c>postCount</c> and,
/// inside <c>posts</c>, <c>order</c>, <c>hook</c>, <c>body</c>, <c>callToAction</c>,
/// <c>hashtags</c>, <c>altText</c>, <c>visualDirection</c>, <c>assetRefs</c>) — the same
/// naming used by the provider structured-output schema, by the persisted canonical draft
/// artifact, and by every other normal-user generation contract. The preview posts are
/// serialized straight from the <see cref="SocialDraft"/>/<see cref="SocialPost"/> CLR types,
/// so they follow whatever naming policy the handler chooses; emitting their .NET member
/// casing makes the posts unreadable downstream and a successfully persisted Social
/// generation can never render its preview.
/// </summary>
public sealed class SocialGenerationResultPayloadTests : IClassFixture<SocialGenerationApiFactory>
{
    private static readonly string[] CanonicalResultProperties = ["socialType", "platform", "title", "language", "postCount", "posts"];
    private static readonly string[] CanonicalPostProperties = ["order", "hook", "body", "callToAction", "hashtags", "altText", "visualDirection", "assetRefs"];

    private readonly SocialGenerationApiFactory factory;

    public SocialGenerationResultPayloadTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Successful_social_job_result_uses_canonical_property_names()
    {
        var (job, _) = await CompleteSocialGeneration("Announce the community workshop to local founders.");

        using var document = JsonDocument.Parse(job.ResultJson!);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        // The generic completion path appends the published asset identity to whatever
        // the Social handler produced, so the root contract is a membership contract.
        // Both members below are exactly what the Social Studio state machine needs to
        // leave the completed-unavailable state.
        var rootNames = PropertyNames(root);
        foreach (var name in CanonicalResultProperties.Concat(new[] { "assetId" }))
            Assert.Contains(name, rootNames);

        var posts = root.GetProperty("posts");
        Assert.Equal(JsonValueKind.Array, posts.ValueKind);
        var post = Assert.Single(posts.EnumerateArray());

        // The preview posts must carry the canonical camelCase contract, not the
        // CLR member names of SocialPost.
        Assert.Equal(CanonicalPostProperties, PropertyNames(post));

        Assert.Equal(1, post.GetProperty("order").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(post.GetProperty("hook").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(post.GetProperty("body").GetString()));
        Assert.Contains("#taslim", post.GetProperty("hashtags").EnumerateArray().Select(tag => tag.GetString()));
    }

    [Fact]
    public async Task Social_job_result_and_persisted_draft_share_the_same_property_names()
    {
        var (job, draftJson) = await CompleteSocialGeneration("Share the launch update with our newsletter readers.");

        using var resultDocument = JsonDocument.Parse(job.ResultJson!);
        using var draftDocument = JsonDocument.Parse(draftJson);
        var resultPost = resultDocument.RootElement.GetProperty("posts")[0];
        var draftPost = draftDocument.RootElement.GetProperty("posts")[0];

        // The Studio preview and the downloadable draft artifact are two renderings of
        // the same canonical post. A naming divergence between them means one reader is
        // guaranteed to receive an unusable payload.
        Assert.Equal(PropertyNames(draftPost), PropertyNames(resultPost));
        Assert.Equal(draftPost.GetProperty("hook").GetString(), resultPost.GetProperty("hook").GetString());
    }

    private static string[] PropertyNames(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();

    private async Task<(GenerationJobDto Job, string DraftJson)> CompleteSocialGeneration(string prompt)
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt,
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            includeHashtags = true,
            includeEmojis = false,
            generateVariants = false,
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.False(string.IsNullOrWhiteSpace(terminal.ResultJson));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.Include(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
        Assert.Equal(AssetTypes.Social, asset.AssetType);
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var stream = await storage.OpenReadAsync(asset.StoredFile!.StorageKey) ?? throw new InvalidOperationException("The canonical Social draft artifact could not be read.");
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            return (terminal, await reader.ReadToEndAsync());
        }
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, new
        {
            displayName = "Social Payload Tester",
            email = $"social-payload-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/social-generation/jobs", string? idempotencyKey = "social-payload-test-request")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Social job did not reach a terminal state.");
    }
}
