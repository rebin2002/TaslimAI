using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Guards the presentation export *metadata* boundary. PR #723 already proves the
/// generated PPTX blob is only downloadable from its own workspace. This covers the
/// distinct readback surface the export depends on: the job execution record, whose
/// result JSON carries the asset id, representation ids and representation file name,
/// plus the job list surface that enumerates those same records.
/// A presentation job without a project publishes a creator-private asset (see
/// <c>AssetVisibility</c>), so its export references must not be readable by anyone
/// who cannot read the asset itself. Jobs bound to a project keep the intentional
/// workspace sharing surface.
/// </summary>
public sealed class PresentationExportMetadataBoundaryTests : IClassFixture<PresentationGenerationApiFactory>
{
    private readonly PresentationGenerationApiFactory factory;

    public PresentationExportMetadataBoundaryTests(PresentationGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Private_presentation_export_references_are_not_readable_through_job_metadata_by_a_workspace_member()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Private Export Owner");
        const string privateTitle = "Board briefing private export";
        var created = await CreatePresentationJob(owner, ownerAuth.PersonalWorkspace.Id, null, privateTitle);
        var terminal = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);

        // The creator's own record is the reference that carries export metadata.
        Assert.Contains("assetId", terminal.ResultJson, StringComparison.Ordinal);
        var assetId = ReadAssetId(terminal.ResultJson!);
        Assert.NotEqual(Guid.Empty, assetId);

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Same Workspace Member");
        await AddMember(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        using var deniedRecord = await member.GetAsync($"/api/generation/jobs/{created.Job.Id}");
        Assert.Equal(HttpStatusCode.NotFound, deniedRecord.StatusCode);
        var deniedBody = await deniedRecord.Content.ReadAsStringAsync();
        Assert.DoesNotContain("assetId", deniedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(".pptx", deniedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privateTitle, deniedBody, StringComparison.Ordinal);

        // The list surface must not enumerate a private export record either.
        var listed = await member.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={ownerAuth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.PresentationGenerate}&pageSize=50");
        Assert.NotNull(listed);
        Assert.DoesNotContain(listed.Items, item => item.Id == created.Job.Id);

        // Acting on a record that is not readable is refused rather than silently exposed.
        using var deniedRetry = await SendWithCsrf(member, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, deniedRetry.StatusCode);

        // Export metadata of a creator-private asset is not readable on the asset surface either.
        using var deniedAsset = await member.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.NotFound, deniedAsset.StatusCode);

        // The owner keeps full readback of their own export record.
        using var ownerRecord = await owner.GetAsync($"/api/generation/jobs/{created.Job.Id}");
        Assert.Equal(HttpStatusCode.OK, ownerRecord.StatusCode);
        var ownerDto = await ownerRecord.Content.ReadFromJsonAsync<GenerationJobDto>();
        Assert.NotNull(ownerDto);
        Assert.Contains("assetId", ownerDto.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_shared_presentation_export_references_stay_readable_through_job_metadata()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Shared Export Owner");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, "Board Room Exports");
        const string sharedTitle = "Board briefing shared export";
        var created = await CreatePresentationJob(owner, ownerAuth.PersonalWorkspace.Id, project.Id, sharedTitle);
        var terminal = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        var assetId = ReadAssetId(terminal.ResultJson!);

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, "Shared Workspace Member");
        await AddMember(ownerAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        using var sharedRecord = await member.GetAsync($"/api/generation/jobs/{created.Job.Id}");
        Assert.Equal(HttpStatusCode.OK, sharedRecord.StatusCode);
        var sharedDto = await sharedRecord.Content.ReadFromJsonAsync<GenerationJobDto>();
        Assert.NotNull(sharedDto);
        Assert.Contains("assetId", sharedDto.ResultJson, StringComparison.Ordinal);

        using var sharedAsset = await member.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, sharedAsset.StatusCode);
    }

    private static Guid ReadAssetId(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return document.RootElement.GetProperty("assetId").GetGuid();
    }

    private static async Task<CreatePresentationGenerationResponse> CreatePresentationJob(
        HttpClient client, Guid workspaceId, Guid? projectId, string title)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/presentation-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", await CsrfToken(client));
        request.Headers.Add("Idempotency-Key", $"presentation-export-metadata-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(new
        {
            workspaceId,
            projectId,
            title,
            description = "A deterministic export used to verify job metadata access.",
            presentationType = "business",
            length = "standard",
            tone = "professional",
            language = "en",
            attachmentIds = Array.Empty<Guid>(),
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!;
    }

    private static async Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", await CsrfToken(client));
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<string> CsrfToken(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return csrf.GetProperty("token").GetString()!;
    }

    private async Task AddMember(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Member,
            JoinedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", await CsrfToken(client));
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = $"presentation-export-metadata-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Presentation export job did not reach a terminal state.");
    }
}
