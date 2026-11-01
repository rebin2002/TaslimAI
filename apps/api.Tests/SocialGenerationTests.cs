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
    public async Task Social_generation_requires_idempotency_key()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "This request must be retry-safe.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        }, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Repeated_social_request_with_same_idempotency_key_returns_one_job()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Create one retry-safe workshop announcement.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        };

        var first = await SendWithCsrf(client, payload, idempotencyKey: "social-idempotency-001");
        var second = await SendWithCsrf(client, payload, idempotencyKey: "social-idempotency-001");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstJob = (await first.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!.Job;
        var secondJob = (await second.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!.Job;
        Assert.Equal(firstJob.Id, secondJob.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == "social-idempotency-001"));
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
    public async Task No_project_social_jobs_are_creator_private_to_workspace_members_but_project_jobs_remain_shared()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner);
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

        var noProjectResponse = await SendWithCsrf(owner, new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            prompt = "Create a creator-private launch announcement.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        }, idempotencyKey: $"social-private-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, noProjectResponse.StatusCode);
        var noProjectJob = (await noProjectResponse.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!.Job;
        var ownerResult = await WaitForTerminal(owner, noProjectJob.Id);
        Assert.Equal("Succeeded", ownerResult.Status);
        Assert.Contains("posts", ownerResult.ResultJson);

        var memberDirect = await member.GetAsync($"/api/generation/jobs/{noProjectJob.Id}");
        Assert.Equal(HttpStatusCode.NotFound, memberDirect.StatusCode);
        Assert.DoesNotContain("posts", await memberDirect.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var memberList = await member.GetFromJsonAsync<GenerationJobListDto>($"/api/generation/jobs?workspaceId={ownerAuth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.SocialGenerate}&pageSize=100");
        Assert.NotNull(memberList);
        Assert.Equal(0, memberList!.TotalCount);
        Assert.Empty(memberList.Items);

        var projectResponse = await SendWithCsrf(owner, new { name = "Shared social project" }, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/projects", $"social-project-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectDto>())!;
        var projectJobResponse = await SendWithCsrf(owner, new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            projectId = project.Id,
            prompt = "Create a project-scoped launch announcement.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        }, idempotencyKey: $"social-project-job-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, projectJobResponse.StatusCode);
        var projectJob = (await projectJobResponse.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!.Job;
        Assert.Equal("Succeeded", (await WaitForTerminal(owner, projectJob.Id)).Status);

        var memberProjectJob = await member.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{projectJob.Id}");
        Assert.NotNull(memberProjectJob);
        Assert.Contains("posts", memberProjectJob!.ResultJson);
        var memberProjectList = await member.GetFromJsonAsync<GenerationJobListDto>($"/api/generation/jobs?workspaceId={ownerAuth.PersonalWorkspace.Id}&projectId={project.Id}&jobType={GenerationJobTypes.SocialGenerate}");
        Assert.NotNull(memberProjectList);
        Assert.Equal(1, memberProjectList!.TotalCount);
        Assert.Equal(projectJob.Id, Assert.Single(memberProjectList.Items).Id);

        var retryJobId = Guid.NewGuid();
        var cancelJobId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            db.GenerationJobs.AddRange(
                new GenerationJob
                {
                    Id = retryJobId,
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    JobType = GenerationJobTypes.SocialGenerate,
                    Status = GenerationJobStatus.Failed,
                    InputJson = "{}",
                    ResultJson = "{\"posts\":[{\"body\":\"creator-private-retry-result\"}]}",
                    CreatedAt = now,
                    FailedAt = now,
                },
                new GenerationJob
                {
                    Id = cancelJobId,
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    JobType = GenerationJobTypes.SocialGenerate,
                    Status = GenerationJobStatus.Queued,
                    InputJson = "{}",
                    ResultJson = "{\"posts\":[{\"body\":\"creator-private-cancel-result\"}]}",
                    CreatedAt = now,
                    QueuedAt = now,
                });
            await db.SaveChangesAsync();
        }

        var retry = await SendJobActionWithCsrf(member, HttpMethod.Post, $"/api/generation/jobs/{retryJobId}/retry");
        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
        Assert.DoesNotContain("creator-private-retry-result", await retry.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var cancel = await SendJobActionWithCsrf(member, HttpMethod.Post, $"/api/generation/jobs/{cancelJobId}/cancel");
        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.DoesNotContain("creator-private-cancel-result", await cancel.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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

    [Fact]
    public async Task Social_job_rejects_selected_asset_without_ready_private_file()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var assetId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                Name = "Stale social reference",
                AssetType = AssetTypes.Image,
                Status = AssetStatus.Active,
                StoredFileId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "Use the selected visual reference safely.",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = new[] { assetId },
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
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

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/social-generation/jobs", string? idempotencyKey = "social-test-request")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJobActionWithCsrf(HttpClient client, HttpMethod method, string path)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
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
