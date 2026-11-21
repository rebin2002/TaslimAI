using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Free-plan generation admission safeguards for Research Studio: attachment
/// identifiers that the server can prove are unsatisfiable (the zero GUID) must
/// be rejected at admission, before a GenerationJob row, a usage reservation,
/// and an ExpensiveAi queue entry are created. Today such requests only fail
/// later inside ResearchGenerationJobHandler.LoadFilesAsync, and a zero GUID
/// even satisfies the "choose a source file" rule because the request
/// validator bounds the attachment count, not the identifiers.
/// </summary>
public sealed class ResearchGenerationAttachmentAdmissionTests : IClassFixture<ResearchApiFactory>
{
    private readonly ResearchApiFactory factory;

    public ResearchGenerationAttachmentAdmissionTests(ResearchApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Research_creation_rejects_zero_guid_attachment_before_queueing()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"research-attachment-admission-{Guid.NewGuid():N}@example.com");

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "A valid research question about renewable energy",
            useWebSources = true,
            attachmentIds = new[] { Guid.Empty },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.ResearchSourceUnavailable, error.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.Equal(0, await db.UsageTransactions.AsNoTracking().CountAsync(usage => usage.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Research_creation_rejects_zero_guid_attachment_when_web_sources_are_disabled()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"research-attachment-no-web-{Guid.NewGuid():N}@example.com");

        // A zero GUID is not a source file, so "web off + only a zero-GUID
        // attachment" must fail closed instead of queueing a job that can
        // never resolve its sources.
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "A valid research question about renewable energy",
            useWebSources = false,
            attachmentIds = new[] { Guid.Empty },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.ResearchSourceUnavailable, error.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(job => job.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Research_creation_still_accepts_an_explicit_empty_attachment_list()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"research-attachment-empty-{Guid.NewGuid():N}@example.com");

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "A valid research question about renewable energy",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Research Admission Tester",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
