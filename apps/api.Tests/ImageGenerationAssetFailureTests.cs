using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationAssetFailureApiFactory : ImageGenerationApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGeneratedAssetPublisher>();
            services.AddScoped<IGeneratedAssetPublisher, FailingImageAssetPublisher>();
        });
    }
}

public sealed class ImageGenerationAssetFailureTests : IClassFixture<ImageGenerationAssetFailureApiFactory>
{
    private readonly ImageGenerationAssetFailureApiFactory factory;

    public ImageGenerationAssetFailureTests(ImageGenerationAssetFailureApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Asset_publication_failure_marks_job_failed_without_output_asset_or_duplicate_usage()
    {
        using var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        register.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        register.Content = JsonContent.Create(new { displayName = "Asset Failure Tester", email = $"image-asset-failure-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        var registration = await client.SendAsync(register);
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var auth = (await registration.Content.ReadFromJsonAsync<AuthResponse>())!;

        csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/image-generation/jobs");
        create.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        create.Content = JsonContent.Create(new { workspaceId = auth.PersonalWorkspace.Id, description = "An asset publication failure test", style = "product", aspectRatio = "square", quality = "standard" });
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
        Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Job.Id));
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }
}

internal sealed class FailingImageAssetPublisher : IGeneratedAssetPublisher
{
    public Task<PreparedGenerationOutput> PrepareAsync(GenerationJob job, GenerationHandlerOutput output, CancellationToken cancellationToken = default) =>
        Task.FromException<PreparedGenerationOutput>(new InvalidOperationException("simulated asset publication failure"));

    public Task DiscardAsync(PreparedGenerationOutput publication, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
