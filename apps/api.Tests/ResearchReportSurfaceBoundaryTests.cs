using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Independent security-review companion for PR #1042 (#936): the creator-private
// Research report asset boundary must hold at every member-facing surface, not only
// at AssetService. These lanes are synthetic authorization tests: freshly registered
// throwaway accounts, seeded in-memory rows, no production data and no live provider.
public sealed class ResearchReportSurfaceBoundaryTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ResearchReportSurfaceBoundaryTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Search_does_not_return_project_shared_research_report_assets_to_co_members()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await (await Register(owner, "Report Surface Owner", $"report-surface-owner-{Guid.NewGuid():N}@example.com"))
            .Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(ownerAuth);
        var project = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth!.PersonalWorkspace.Id}/projects", new { name = "Report surface project" });

        using var member = factory.CreateClient();
        var memberAuth = await (await Register(member, "Report Surface Member", $"report-surface-member-{Guid.NewGuid():N}@example.com"))
            .Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(memberAuth);

        Guid reportAssetId;
        Guid sharedAssetId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = memberAuth!.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });

            var now = DateTime.UtcNow;
            // Backing files are project-shared (no conversation), so the pre-existing
            // stored-file clause lets both assets through; only the #936 provenance
            // guard can discriminate. The shared File asset is the positive control.
            var reportFile = NewReadyFile(ownerAuth.PersonalWorkspace.Id, ownerAuth.User.Id, now, project.Id);
            var sharedFile = NewReadyFile(ownerAuth.PersonalWorkspace.Id, ownerAuth.User.Id, now, project.Id);
            db.StoredFiles.AddRange(reportFile, sharedFile);
            reportAssetId = Guid.NewGuid();
            sharedAssetId = Guid.NewGuid();
            db.Assets.AddRange(
                new Asset
                {
                    Id = reportAssetId,
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    ProjectId = project.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    StoredFileId = reportFile.Id,
                    Name = "Surface convergence marker report",
                    AssetType = AssetTypes.Research,
                    MimeType = reportFile.ContentType,
                    Status = AssetStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new Asset
                {
                    Id = sharedAssetId,
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    ProjectId = project.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    StoredFileId = sharedFile.Id,
                    Name = "Surface convergence marker shared",
                    AssetType = AssetTypes.File,
                    MimeType = sharedFile.ContentType,
                    Status = AssetStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        var ownerSearch = await owner.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=surface%20convergence%20marker");
        Assert.NotNull(ownerSearch);
        var ownerAssetIds = ownerSearch!.Groups
            .Where(group => group.Type == GlobalSearchResultTypes.Asset)
            .SelectMany(group => group.Items)
            .Select(item => item.Id)
            .ToHashSet();
        Assert.Contains(reportAssetId, ownerAssetIds);
        Assert.Contains(sharedAssetId, ownerAssetIds);

        var memberSearch = await member.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=surface%20convergence%20marker");
        Assert.NotNull(memberSearch);
        var memberAssetIds = memberSearch!.Groups
            .Where(group => group.Type == GlobalSearchResultTypes.Asset)
            .SelectMany(group => group.Items)
            .Select(item => item.Id)
            .ToHashSet();
        Assert.Contains(sharedAssetId, memberAssetIds);
        Assert.DoesNotContain(reportAssetId, memberAssetIds);
    }

    [Fact]
    public async Task Project_overview_asset_count_excludes_creator_private_report_assets_for_co_members()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await (await Register(owner, "Overview Surface Owner", $"overview-surface-owner-{Guid.NewGuid():N}@example.com"))
            .Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(ownerAuth);
        var project = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth!.PersonalWorkspace.Id}/projects", new { name = "Overview surface project" });

        using var member = factory.CreateClient();
        var memberAuth = await (await Register(member, "Overview Surface Member", $"overview-surface-member-{Guid.NewGuid():N}@example.com"))
            .Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(memberAuth);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = memberAuth!.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            var now = DateTime.UtcNow;
            var reportFile = NewReadyFile(ownerAuth.PersonalWorkspace.Id, ownerAuth.User.Id, now, project.Id);
            var sharedFile = NewReadyFile(ownerAuth.PersonalWorkspace.Id, ownerAuth.User.Id, now, project.Id);
            db.StoredFiles.AddRange(reportFile, sharedFile);
            db.Assets.AddRange(
                new Asset
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    ProjectId = project.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    StoredFileId = reportFile.Id,
                    Name = "Overview surface report asset",
                    AssetType = AssetTypes.Research,
                    MimeType = reportFile.ContentType,
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
                    StoredFileId = sharedFile.Id,
                    Name = "Overview surface shared asset",
                    AssetType = AssetTypes.File,
                    MimeType = sharedFile.ContentType,
                    Status = AssetStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        var ownerOverview = await owner.GetFromJsonAsync<ProjectOverviewDto>($"/api/projects/{project.Id}/overview");
        Assert.NotNull(ownerOverview);
        Assert.Equal(2, ownerOverview!.Counts.Assets);

        var memberOverview = await member.GetFromJsonAsync<ProjectOverviewDto>($"/api/projects/{project.Id}/overview");
        Assert.NotNull(memberOverview);
        Assert.Equal(1, memberOverview!.Counts.Assets);
    }

    private static StoredFile NewReadyFile(Guid workspaceId, Guid userId, DateTime createdAt, Guid projectId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = userId,
        ProjectId = projectId,
        ConversationId = null,
        OriginalFileName = $"{Guid.NewGuid():N}.txt",
        StoredFileName = $"{Guid.NewGuid():N}.txt",
        ContentType = "text/plain",
        Extension = ".txt",
        SizeBytes = 32,
        StorageProvider = FileStorageProviders.Local,
        StorageKey = $"surface-boundary-tests/{Guid.NewGuid():N}.txt",
        Status = StoredFileStatus.Ready,
        TextExtractionStatus = FileExtractionStatus.Ready,
        CreatedAt = createdAt,
        ProcessedAt = createdAt,
    };

    private static Task<HttpResponseMessage> Register(HttpClient client, string displayName, string email) =>
        SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email, password = "StrongPassword!123", preferredLanguage = "en" });

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
