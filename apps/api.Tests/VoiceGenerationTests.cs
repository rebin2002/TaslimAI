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

public sealed class VoiceGenerationTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public VoiceGenerationTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Voice_endpoint_creates_durable_job_and_safe_unavailable_result_without_asset_or_customer_charge()
    {
        using var client = factory.CreateClient();
        var authResponse = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Voice Tester",
            email = $"voice-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "ku",
        });
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var auth = (await authResponse.Content.ReadFromJsonAsync<AuthResponse>())!;

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "بەخێربێن بۆ تسلیم",
            language = "ku",
            voiceStyle = "warm",
            speakingStyle = "clear",
            instructions = "Speak clearly.",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateVoiceGenerationResponse>())!;
        Assert.Equal(GenerationJobTypes.VoiceGenerate, created.Job.JobType);

        GenerationJobDto? terminal = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            terminal = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
            if (terminal?.Status is "Succeeded" or "Failed" or "Cancelled") break;
            await Task.Delay(50);
        }

        Assert.NotNull(terminal);
        Assert.Equal("Failed", terminal!.Status);
        Assert.Equal(GenerationJobErrorCodes.VoiceProviderUnavailable, terminal.ErrorCode);
        Assert.DoesNotContain("openai", terminal.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(UsageFeature.Voice, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
