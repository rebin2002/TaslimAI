using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationFailureApiFactory : ImageGenerationApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IImageGenerationProvider>();
            services.AddSingleton<IImageGenerationProvider, SafetyRefusingImageProvider>();
        });
    }
}

public sealed class ImageGenerationFailureTests : IClassFixture<ImageGenerationFailureApiFactory>
{
    private readonly ImageGenerationFailureApiFactory factory;

    public ImageGenerationFailureTests(ImageGenerationFailureApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Safety_refusal_is_safe_and_publishes_no_output_or_asset()
    {
        using var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        register.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        register.Content = JsonContent.Create(new { displayName = "Safety Tester", email = $"image-safety-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        var registration = await client.SendAsync(register);
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var auth = (await registration.Content.ReadFromJsonAsync<AuthResponse>())!;

        csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/image-generation/jobs");
        create.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        create.Content = JsonContent.Create(new { workspaceId = auth.PersonalWorkspace.Id, description = "A safe test description", style = "product", aspectRatio = "square", quality = "standard" });
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
        Assert.Equal("IMAGE_SAFETY_REFUSAL", terminal.ErrorCode);
        Assert.Equal("This request could not be completed by the image safety system. Try a different description.", terminal.ErrorMessage);
        Assert.Empty(terminal.Outputs);
        Assert.DoesNotContain("moderation", terminal.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }
}

internal sealed class SafetyRefusingImageProvider : IImageGenerationProvider
{
    public string Key => "openai";

    public Task<ImageProviderResult> GenerateAsync(ImageGenerationInput request, ImagePromptBuildResult prompt, CancellationToken cancellationToken = default) =>
        throw new ImageProviderSafetyException();
}
