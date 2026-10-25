using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchProjectOverviewPrivacyTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ResearchProjectOverviewPrivacyTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Project_overview_hides_creator_private_research_records_from_workspace_members()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Overview Research Owner", $"overview-research-owner-{Guid.NewGuid():N}@example.com");
        var project = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/projects", new
        {
            name = "Research overview privacy project",
            type = ProjectTypes.Research,
        });

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Overview Research Member", $"overview-research-member-{Guid.NewGuid():N}@example.com");
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

            var now = DateTime.UtcNow;
            var privateResearchJob = new GenerationJob
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = ownerAuth.User.Id,
                JobType = GenerationJobTypes.ResearchGenerate,
                Status = GenerationJobStatus.Succeeded,
                Title = "Private Research report marker",
                InputJson = "{}",
                ResultJson = "{\"assetId\":\"private-report\"}",
                ProgressPercent = 100,
                CreatedAt = now,
                CompletedAt = now,
            };
            var sharedJob = new GenerationJob
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = ownerAuth.User.Id,
                JobType = GenerationJobTypes.SystemTest,
                Status = GenerationJobStatus.Succeeded,
                Title = "Shared system activity marker",
                InputJson = "{}",
                ProgressPercent = 100,
                CreatedAt = now.AddSeconds(-1),
                CompletedAt = now.AddSeconds(-1),
            };
            db.GenerationJobs.AddRange(privateResearchJob, sharedJob);
            db.Assets.AddRange(
                new Asset
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    ProjectId = project.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    SourceGenerationJobId = privateResearchJob.Id,
                    Name = "Private Research report asset marker",
                    Description = "Creator-private Research report.",
                    AssetType = AssetTypes.Research,
                    MimeType = "application/pdf",
                    Status = AssetStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new Asset
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    ProjectId = project.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    SourceGenerationJobId = sharedJob.Id,
                    Name = "Shared system asset marker",
                    Description = "Workspace-visible non-Research output.",
                    AssetType = AssetTypes.File,
                    MimeType = "application/json",
                    Status = AssetStatus.Active,
                    CreatedAt = now.AddSeconds(-1),
                    UpdatedAt = now.AddSeconds(-1),
                });
            await db.SaveChangesAsync();
        }

        var ownerOverview = await owner.GetFromJsonAsync<ProjectOverviewDto>($"/api/projects/{project.Id}/overview");
        Assert.NotNull(ownerOverview);
        Assert.Equal(2, ownerOverview!.Counts.Activity);
        Assert.Equal(2, ownerOverview.Counts.Assets);
        Assert.Contains(ownerOverview.RecentActivity, item => item.Title == "Private Research report marker");
        Assert.Contains(ownerOverview.RecentActivity, item => item.Title == "Shared system activity marker");

        var memberOverviewResponse = await member.GetAsync($"/api/projects/{project.Id}/overview");
        Assert.Equal(HttpStatusCode.OK, memberOverviewResponse.StatusCode);
        var memberOverview = await memberOverviewResponse.Content.ReadFromJsonAsync<ProjectOverviewDto>();
        Assert.NotNull(memberOverview);
        Assert.Equal(1, memberOverview!.Counts.Activity);
        Assert.Equal(1, memberOverview.Counts.Assets);
        Assert.DoesNotContain(memberOverview.RecentActivity, item => item.Title == "Private Research report marker");
        Assert.Contains(memberOverview.RecentActivity, item => item.Title == "Shared system activity marker");
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName, string email)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object body)
    {
        using var response = await SendWithCsrf(client, method, path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}
