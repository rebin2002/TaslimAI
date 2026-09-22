using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationStorageFailureApiFactory : ImageGenerationApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService, FailingGeneratedImageStorage>();
        });
    }
}

public sealed class ImageGenerationStorageFailureTests : IClassFixture<ImageGenerationStorageFailureApiFactory>
{
    private readonly ImageGenerationStorageFailureApiFactory factory;

    public ImageGenerationStorageFailureTests(ImageGenerationStorageFailureApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Storage_failure_marks_job_failed_without_publishing_an_asset()
    {
        using var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        register.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        register.Content = JsonContent.Create(new { displayName = "Storage Tester", email = $"image-storage-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        var registration = await client.SendAsync(register);
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var auth = (await registration.Content.ReadFromJsonAsync<AuthResponse>())!;

        csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/image-generation/jobs");
        create.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        create.Content = JsonContent.Create(new { workspaceId = auth.PersonalWorkspace.Id, description = "A storage failure test", style = "product", aspectRatio = "square", quality = "standard" });
        var createdResponse = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<CreateImageGenerationResponse>())!;

        GenerationJobDto? terminal = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            terminal = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
            if (terminal?.Status is "Succeeded" or "Failed" or "Cancelled") break;
            await Task.Delay(50);
        }
        Assert.NotNull(terminal);
        Assert.Equal("Failed", terminal!.Status);
        Assert.Equal("IMAGE_OUTPUT_STORAGE_FAILED", terminal.ErrorCode);
        Assert.Equal("The image was generated but could not be saved. Please try again.", terminal.ErrorMessage);
        Assert.Empty(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
        Assert.True(await db.StoredFiles.AsNoTracking().AnyAsync(item => item.Status == StoredFileStatus.Failed));
    }
}

internal sealed class FailingGeneratedImageStorage : IFileStorageService
{
    public string ProviderKey => "test-failing-storage";
    public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => throw new FileStorageOperationException();
    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
