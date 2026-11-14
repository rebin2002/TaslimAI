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

public sealed class PresentationExportWorkspaceIsolationTests : IClassFixture<PresentationGenerationApiFactory>
{
    private readonly PresentationGenerationApiFactory factory;

    public PresentationExportWorkspaceIsolationTests(PresentationGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Generated_pptx_representation_is_downloadable_only_from_its_workspace()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Presentation Export Owner");
        var csrf = await owner.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/presentation-generation/jobs");
        createRequest.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        createRequest.Headers.Add("Idempotency-Key", $"presentation-export-{Guid.NewGuid():N}");
        createRequest.Content = JsonContent.Create(new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            title = "Workspace-private presentation",
            description = "A deterministic export used to verify workspace access.",
            presentationType = "business",
            length = "standard",
            tone = "professional",
            language = "en",
            attachmentIds = Array.Empty<Guid>(),
        });

        using var createResponse = await owner.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!;
        var terminal = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);

        Guid assetId;
        Guid representationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets
                .Include(item => item.Representations)
                .SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
            Assert.Equal(AssetTypes.Presentation, asset.AssetType);
            var representation = Assert.Single(asset.Representations);
            Assert.Equal(AssetRepresentationTypes.Pptx, representation.RepresentationType);
            assetId = asset.Id;
            representationId = representation.Id;
        }

        var downloadPath = $"/api/assets/{assetId}/representations/{representationId}/download";
        using var ownerDownload = await owner.GetAsync(downloadPath);
        Assert.Equal(HttpStatusCode.OK, ownerDownload.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", ownerDownload.Content.Headers.ContentType?.MediaType);
        var exportedPptx = await ownerDownload.Content.ReadAsByteArrayAsync();
        Assert.True(exportedPptx.Length > 500);
        Assert.Equal((byte)'P', exportedPptx[0]);
        Assert.Equal((byte)'K', exportedPptx[1]);

        using var otherWorkspace = factory.CreateClient();
        _ = await Register(otherWorkspace, "Unrelated Workspace User");
        using var deniedDownload = await otherWorkspace.GetAsync(downloadPath);
        Assert.Equal(HttpStatusCode.NotFound, deniedDownload.StatusCode);
        var deniedBody = await deniedDownload.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Workspace-private presentation", deniedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("pptx", deniedBody, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = $"presentation-export-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
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

        throw new TimeoutException("Presentation export job did not reach a terminal state.");
    }
}
