using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AssetTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public AssetTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task System_test_publishes_project_asset_with_file_provenance_and_secure_download()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Asset Publisher");
        var project = await CreateProject(client, auth.PersonalWorkspace.Id, "Asset Project");
        var job = await CreateJob(client, auth.PersonalWorkspace.Id, project.Id, "Project artifact");
        await WaitForTerminal(client, job.Id);

        var list = await client.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&projectId={project.Id}&assetType=file&status=Active&page=1&pageSize=10");
        var asset = Assert.Single(list!.Items);
        Assert.Equal("Project artifact", asset.Name);
        Assert.Equal(project.Id, asset.ProjectId);
        Assert.Equal("Asset Project", asset.ProjectName);
        Assert.Equal("file", asset.AssetType);
        Assert.Equal("application/json", asset.MimeType);
        Assert.True(asset.HasFile);
        Assert.Equal("system", asset.SourceStudio);
        Assert.Equal("Project artifact", asset.SourceJobTitle);
        Assert.NotNull(asset.FileSizeBytes);

        var download = await client.GetAsync($"/api/assets/{asset.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/json", download.Content.Headers.ContentType?.MediaType);
        Assert.Contains("system.test", await download.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var persisted = await db.Assets.AsNoTracking().Include(item => item.StoredFile).SingleAsync(item => item.Id == asset.Id);
        Assert.Equal(job.Id, persisted.SourceGenerationJobId);
        Assert.Equal(project.Id, persisted.ProjectId);
        Assert.NotNull(persisted.StoredFileId);
        Assert.Equal(StoredFileStatus.Ready, persisted.StoredFile!.Status);
        var output = await db.GenerationJobOutputs.AsNoTracking().SingleAsync(item => item.GenerationJobId == job.Id);
        Assert.Equal(persisted.StoredFileId, output.StoredFileId);
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{job.Id:N}");
        Assert.Equal(0m, usage.ProviderCostUsd);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact]
    public async Task Asset_library_supports_search_filters_pagination_rename_project_move_archive_and_restore()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Asset Librarian");
        var project = await CreateProject(client, auth.PersonalWorkspace.Id, "Library Project");
        var firstJob = await CreateJob(client, auth.PersonalWorkspace.Id, null, "Quarterly result");
        var secondJob = await CreateJob(client, auth.PersonalWorkspace.Id, null, "Another result");
        await WaitForTerminal(client, firstJob.Id);
        await WaitForTerminal(client, secondJob.Id);

        var page = await client.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Active&page=1&pageSize=1");
        Assert.Single(page!.Items);
        Assert.True(page.TotalCount >= 2);
        Assert.True(page.TotalPages >= 2);

        var named = await client.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Active&sort=name&page=1&pageSize=10");
        Assert.Equal(named!.Items.OrderBy(item => item.Name).Select(item => item.Id), named.Items.Select(item => item.Id));
        var invalidSort = await client.GetAsync($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Active&sort=provider");
        Assert.Equal(HttpStatusCode.BadRequest, invalidSort.StatusCode);
        var invalidSortBody = await invalidSort.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ASSET_SORT_NOT_SUPPORTED", invalidSortBody.GetProperty("error").GetProperty("code").GetString());

        var search = await client.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Active&assetType=file&search=Quarterly");
        var asset = Assert.Single(search!.Items);
        var renamed = await SendWithCsrf<AssetDto>(client, HttpMethod.Patch, $"/api/assets/{asset.Id}", new { name = "Quarterly archive", description = "Reusable result", projectId = project.Id });
        Assert.Equal("Quarterly archive", renamed.Name);
        Assert.Equal("Reusable result", renamed.Description);
        Assert.Equal(project.Id, renamed.ProjectId);

        var projectAssets = await client.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&projectId={project.Id}&status=Active");
        Assert.Contains(projectAssets!.Items, item => item.Id == asset.Id);

        var removed = await SendWithCsrf<AssetDto>(client, HttpMethod.Patch, $"/api/assets/{asset.Id}", new { name = renamed.Name, description = renamed.Description, projectId = (Guid?)null });
        Assert.Null(removed.ProjectId);

        var archived = await SendWithCsrf<AssetDto>(client, HttpMethod.Post, $"/api/assets/{asset.Id}/archive", null);
        Assert.Equal("Archived", archived.Status);
        var active = await client.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Active&search=Quarterly");
        Assert.DoesNotContain(active!.Items, item => item.Id == asset.Id);
        var archivedList = await client.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Archived&search=Quarterly");
        Assert.Contains(archivedList!.Items, item => item.Id == asset.Id);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var storedFile = await db.Assets.AsNoTracking().Where(item => item.Id == asset.Id).Select(item => item.StoredFile!).SingleAsync();
            Assert.Equal(StoredFileStatus.Ready, storedFile.Status);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IFileStorageService>().ExistsAsync(storedFile.StorageKey));
        }

        var restored = await SendWithCsrf<AssetDto>(client, HttpMethod.Post, $"/api/assets/{asset.Id}/restore", null);
        Assert.Equal("Active", restored.Status);
    }

    [Fact]
    public async Task Asset_get_update_download_and_project_assignment_are_workspace_isolated()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Asset Owner");
        var job = await CreateJob(owner, ownerAuth.PersonalWorkspace.Id, null, "Private artifact");
        await WaitForTerminal(owner, job.Id);
        var ownerAssets = await owner.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}");
        var asset = Assert.Single(ownerAssets!.Items, item => item.Name == "Private artifact");

        using var other = factory.CreateClient();
        var otherAuth = await Register(other, "Other Asset User");
        var otherProject = await CreateProject(other, otherAuth.PersonalWorkspace.Id, "Other Project");

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/assets?workspaceId={ownerAuth.PersonalWorkspace.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/assets/{asset.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/assets/{asset.Id}/download")).StatusCode);
        var update = await SendWithCsrf(other, HttpMethod.Patch, $"/api/assets/{asset.Id}", new { name = "Stolen", projectId = otherProject.Id });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        var archive = await SendWithCsrf(other, HttpMethod.Post, $"/api/assets/{asset.Id}/archive", null);
        Assert.Equal(HttpStatusCode.NotFound, archive.StatusCode);
        var otherList = await other.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={otherAuth.PersonalWorkspace.Id}&search=Private");
        Assert.Empty(otherList!.Items);

        var invalidMove = await SendWithCsrf(owner, HttpMethod.Patch, $"/api/assets/{asset.Id}", new { name = asset.Name, projectId = otherProject.Id });
        Assert.Equal(HttpStatusCode.BadRequest, invalidMove.StatusCode);
        var body = await invalidMove.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PROJECT_NOT_IN_WORKSPACE", body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"asset-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name) =>
        SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });

    private static Task<GenerationJobDto> CreateJob(HttpClient client, Guid workspaceId, Guid? projectId, string title) =>
        SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new { workspaceId, projectId, jobType = "system.test", title, inputJson = "{}" });

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            if (job!.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {id} did not reach a terminal state.");
    }

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
