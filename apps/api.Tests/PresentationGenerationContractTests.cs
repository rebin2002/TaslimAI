using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PresentationGenerationContractTests
{
    [Fact]
    public void Explicit_null_attachment_ids_are_treated_like_an_empty_source_selection()
    {
        var request = new PresentationGenerationRequest
        {
            WorkspaceId = Guid.NewGuid(),
            Title = "Quarterly plan",
            Description = "Present the quarterly operating plan.",
            AttachmentIds = null,
        };

        var input = PresentationGenerationContractMapper.ToInput(request);

        PresentationGenerationRequestValidator.Validate(input, new PresentationGenerationOptions());
        Assert.Empty(input.AttachmentIds);
    }
}

public sealed class PresentationGenerationNullAttachmentApiTests : IClassFixture<PresentationGenerationApiFactory>
{
    private readonly PresentationGenerationApiFactory factory;

    public PresentationGenerationNullAttachmentApiTests(PresentationGenerationApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Http_request_with_null_attachment_ids_creates_a_job_without_mapper_failure()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "Create a concise quarterly operating plan.",
            presentationType = "business",
            attachmentIds = (Guid[]?)null,
        }, $"presentation-null-attachments-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new { displayName = "Presentation Null Attachment Tester", email = $"presentation-null-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string idempotencyKey)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/presentation-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload);
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

        throw new TimeoutException("Presentation job did not reach a terminal state.");
    }
}
