using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// QA boundary regression for the PR #879 document-create admission guard: a malformed
/// (whitespace-only) Idempotency-Key combined with an UNAUTHORIZED or INVALID request.
///
/// The four facts in <see cref="DocumentGenerationIdempotencyBoundaryTests"/> always present a
/// well-formed, authenticated, CSRF-valid request and vary only the key. That leaves the
/// precedence of the key guard against the outer security layers untested:
///
/// 1. Authentication must win. A whitespace key may never make an unauthenticated caller's
///    submission reach the action body - the response must be the 401 challenge from endpoint
///    authorization, and it must not leak the IDEMPOTENCY_KEY_REQUIRED contract decision to a
///    caller that has not proven identity.
/// 2. The key guard must still precede field validation. Once authenticated, a whitespace key
///    with an invalid body (blank title) must fail with IDEMPOTENCY_KEY_REQUIRED, not the
///    DOCUMENT_REQUEST_INVALID field error, because retry safety is checked before the request is
///    ever parsed into an input.
/// In every case the private ephemeral SQLite fixture (deterministic provider, no network, no
/// real provider call) must gain zero GenerationJobs rows and zero UsageTransactions rows: a
/// rejected malformed key must never queue work or record usage, no matter what else is wrong
/// with the request. No customer charge can occur - the assertions below make persistence
/// structurally impossible to miss by counting the whole fixture database, not one workspace.
/// </summary>
public sealed class DocumentGenerationKeyAbusePrecedenceQaTests : IClassFixture<DocumentGenerationApiFactory>
{
    private const string WhitespaceOnlyKey = "   ";

    private readonly DocumentGenerationApiFactory factory;
    private readonly int baselineGenerationJobs;
    private readonly int baselineUsageTransactions;

    public DocumentGenerationKeyAbusePrecedenceQaTests(DocumentGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.Database.EnsureCreated();
        baselineGenerationJobs = db.GenerationJobs.AsNoTracking().Count();
        baselineUsageTransactions = db.UsageTransactions.AsNoTracking().Count();
    }

    [Fact]
    public async Task Whitespace_key_from_unauthenticated_caller_fails_at_authorization_without_reaching_the_key_guard_or_persisting_anything()
    {
        using var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");

        // A valid-looking body and a well-formed CSRF token, but no authentication cookie session:
        // endpoint authorization must reject before the action's idempotency guard ever runs.
        var response = await Send(client, Guid.NewGuid(), WhitespaceOnlyKey, title: "Retry safety brief", csrfToken: csrf.GetProperty("token").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("IDEMPOTENCY_KEY_REQUIRED", body);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task Whitespace_key_from_unauthenticated_caller_without_csrf_token_still_fails_closed_at_authorization_and_persists_nothing()
    {
        using var client = factory.CreateClient();

        // Neither authentication nor CSRF: the request must still be refused by the outer security
        // layer and must never be answered with the key-guard contract.
        var response = await Send(client, Guid.NewGuid(), WhitespaceOnlyKey, title: "Retry safety brief", csrfToken: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("IDEMPOTENCY_KEY_REQUIRED", body);
        AssertNothingWasPersisted();
    }

    [Fact]
    public async Task Whitespace_key_with_invalid_body_is_rejected_as_key_error_before_field_validation_and_persists_nothing()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");

        // Authenticated, CSRF-valid, but the body is invalid (blank title) AND the key is
        // whitespace-only. The admission guard runs before mapping/validation, so the caller
        // receives the retry-safety contract, not the field error - and nothing is queued.
        var response = await Send(client, auth.PersonalWorkspace.Id, WhitespaceOnlyKey, title: " ", csrfToken: csrf.GetProperty("token").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var code = body.GetProperty("error").GetProperty("code").GetString();
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", code);
        AssertNotEqualFieldValidationCode(code);
        AssertNothingWasPersisted();
    }

    private static void AssertNotEqualFieldValidationCode(string? code)
    {
        Assert.DoesNotContain(code ?? string.Empty, "DOCUMENT_REQUEST_INVALID", StringComparison.Ordinal);
    }

    private void AssertNothingWasPersisted()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(baselineGenerationJobs, db.GenerationJobs.AsNoTracking().Count());
        Assert.Equal(baselineUsageTransactions, db.UsageTransactions.AsNoTracking().Count());
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, Guid workspaceId, string idempotencyKey, string title, string? csrfToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/document-generation/jobs")
        {
            Content = JsonContent.Create(new
            {
                workspaceId,
                title,
                description = "Create a concise professional brief that proves retry safety.",
                documentType = "report",
                length = "short",
                tone = "professional",
                language = "en",
                outputFormat = "docx",
                attachmentIds = Array.Empty<Guid>(),
            }),
        };
        if (csrfToken is not null)
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Key Abuse Precedence QA",
                email = $"key-abuse-precedence-qa-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}
