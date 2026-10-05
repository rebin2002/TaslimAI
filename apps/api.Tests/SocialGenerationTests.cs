using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Ai;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialGenerationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("SocialGeneration:Enabled", "true");
        builder.UseSetting("SocialGeneration:ProviderTimeoutSeconds", "10");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISocialGenerationProvider>();
            services.AddSingleton<ISocialGenerationProvider, DeterministicSocialProvider>();
        });
    }
}

public sealed class SocialGenerationTests : IClassFixture<SocialGenerationApiFactory>
{
    private readonly SocialGenerationApiFactory factory;
    public SocialGenerationTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Social_job_publishes_one_json_asset_and_zero_customer_charge()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Announce our new customer workshop to local business owners.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "ku",
            includeHashtags = true,
            includeEmojis = false,
            generateVariants = true,
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Contains("posts", terminal.ResultJson);
        Assert.Single(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.Include(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
        Assert.Equal(AssetTypes.Social, asset.AssetType);
        Assert.Equal(StoredFileStatus.Ready, asset.StoredFile!.Status);
        Assert.Equal("application/json", asset.StoredFile.ContentType);
        Assert.Contains("postCount", asset.MetadataJson);
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(UsageFeature.Social, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact]
    public async Task Social_creation_requires_authentication_and_workspace_isolation()
    {
        using var anonymous = factory.CreateClient();
        var unauthorized = await anonymous.PostAsJsonAsync("/api/social-generation/jobs", new { workspaceId = Guid.NewGuid(), prompt = "Hello social" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var first = factory.CreateClient();
        var firstAuth = await Register(first);
        using var second = factory.CreateClient();
        await Register(second);
        var response = await SendWithCsrf(second, new { workspaceId = firstAuth.PersonalWorkspace.Id, prompt = "Hello social" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Social_job_cannot_use_another_members_conversation_private_source()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner);
        var projectResponse = await SendWithCsrf(owner, new { name = "Private social project" }, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/projects");
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectDto>())!;
        var conversationResponse = await SendWithCsrf(owner, new { projectId = project.Id, title = "Private source conversation" }, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/conversations");
        Assert.Equal(HttpStatusCode.Created, conversationResponse.StatusCode);
        var conversation = (await conversationResponse.Content.ReadFromJsonAsync<ConversationDto>())!;
        var upload = await Upload(owner, ownerAuth.PersonalWorkspace.Id, project.Id, conversation.Id);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var file = (await upload.Content.ReadFromJsonAsync<StoredFileDto>())!;

        using var member = factory.CreateClient();
        var memberAuth = await Register(member);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = memberAuth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(member, new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            projectId = project.Id,
            prompt = "Use the selected source safely.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            attachmentIds = new[] { file.Id },
            assetIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(member, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.SocialContextUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, new { displayName = "Social Tester", email = $"social-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "ku" }, "/api/auth/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/social-generation/jobs")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid workspaceId, Guid projectId, Guid conversationId)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("PRIVATE SOCIAL SOURCE"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "private-social-source.txt");
        form.Add(new StringContent(projectId.ToString()), "projectId");
        form.Add(new StringContent(conversationId.ToString()), "conversationId");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
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

internal sealed class DeterministicSocialProvider : ISocialGenerationProvider
{
    public Task<SocialProviderResult> GenerateAsync(SocialGenerationPrompt prompt, SocialGenerationOptions options, CancellationToken cancellationToken = default)
    {
        var draft = new SocialDraft
        {
            Title = "ڕۆژی کارگەی نوێ",
            Platform = prompt.Input.Platform,
            SocialType = prompt.Input.SocialType,
            Language = "ku",
            Posts = [new SocialPost { Order = 1, Hook = "کارگەیەکی نوێ بۆ کاروبارە بچووکەکان", Body = "لەگەڵمان بەشدار بە بۆ گفتوگۆ و فێربوونی هەنگاوەکانی داهاتوو.", CallToAction = "ئێستا ناوت تۆمار بکە", Hashtags = ["#taslim", "#business"], AltText = "کۆبوونەوەیەکی فێرکاری", VisualDirection = "وێنەیەکی ڕوون و پیشەیی", AssetRefs = [] }]
        };
        return Task.FromResult(new SocialProviderResult(draft, new AiUsageMetadata("social-test", "social-test", 90, 40, 130, 0.001m, 0.001m, 7, "completed", false, PricingVersion: "social-test-v1", Currency: "USD", CostBasis: UsageCostBasis.Actual)));
    }
}
