using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Pins the CSRF rejection contract: every double-submit rejection on an API endpoint
/// must return the machine-readable <c>CSRF_VALIDATION_FAILED</c> envelope, whichever
/// enforcement layer observed the request.
///
/// <c>AntiforgeryValidationMiddleware</c> enumerates the anonymous authentication
/// endpoints by exact path, while MVC routing also accepts trailing-separator and
/// case-insensitive spellings of those endpoints. Before the envelope filter existed, a
/// stale or missing token on a spelling such as <c>POST /api/auth/login/</c> was rejected
/// by the controller-level <c>[ValidateAntiForgeryToken]</c> filter alone, which answered
/// with the generic Problem Details body. That body carries no <c>error.code</c>, so the
/// web API client could not recognize the rejection and never performed its single
/// refresh-and-retry: the caller saw a hard failure instead of a transparent recovery.
///
/// All requests are synthetic; no provider, secret, or customer data is involved.
/// </summary>
public sealed class CsrfRejectionEnvelopeTests : IClassFixture<TaslimApiFactory>
{
    private const string Password = "StrongPassword!123";
    private const string CsrfFailureCode = "CSRF_VALIDATION_FAILED";
    private readonly TaslimApiFactory factory;

    public CsrfRejectionEnvelopeTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData("/api/auth/login/")]                       // trailing separator: routed, middleware skipped
    [InlineData("/API/AUTH/LOGIN")]                        // case variant: routed, middleware skipped
    [InlineData("/api/auth/register/")]
    [InlineData("/API/Auth/Register")]
    public async Task Anonymous_auth_rejection_returns_the_stable_csrf_envelope(string path)
    {
        using var client = factory.CreateClient();
        var payload = path.Contains("register", StringComparison.OrdinalIgnoreCase)
            ? (object)new
            {
                displayName = "Envelope Probe",
                email = $"envelope-{Guid.NewGuid():N}@example.com",
                password = Password,
                preferredLanguage = "en",
            }
            : new { email = "envelope-missing@example.com", password = Password };

        var response = await SendWithoutToken(client, path, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertCsrfEnvelope(await ReadJson(response));
    }

    [Fact]
    public async Task Canonical_anonymous_login_rejection_envelope_is_unchanged()
    {
        using var client = factory.CreateClient();
        var response = await SendWithoutToken(client, "/api/auth/login",
            new { email = "canonical-missing@example.com", password = Password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertCsrfEnvelope(await ReadJson(response));
    }

    [Fact]
    public async Task Authenticated_mutation_without_a_request_token_keeps_the_same_envelope()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "Envelope Owner");

        var response = await SendWithoutToken(client,
            $"/api/workspaces/{auth.GetProperty("personalWorkspace").GetProperty("id").GetString()}/projects",
            new { name = "Needs A Token", type = "Business" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertCsrfEnvelope(await ReadJson(response));
    }

    [Fact]
    public async Task Rejection_body_stays_safe_and_is_not_a_problem_details_document()
    {
        using var client = factory.CreateClient();
        var response = await SendWithoutToken(client, "/api/auth/login/",
            new { email = "leak-check@example.com", password = Password });
        var body = await ReadJson(response);

        Assert.False(body.TryGetProperty("type", out _), "The CSRF rejection must not expose the Problem Details type URI.");
        Assert.False(body.TryGetProperty("title", out _), "The CSRF rejection must not expose the Problem Details title.");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("taslim.csrf", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taslim.auth", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Signature_exempted_intake_is_never_rewritten_as_a_csrf_rejection()
    {
        using var client = factory.CreateClient();
        var response = await SendWithoutToken(client, "/api/autopilot/events",
            new { eventType = "unknown", payload = JsonDocument.Parse("{}").RootElement });

        var body = await ReadJson(response);
        if (body.TryGetProperty("error", out var error)
            && error.TryGetProperty("code", out var code))
        {
            Assert.NotEqual(CsrfFailureCode, code.GetString());
        }
    }

    [Fact]
    public async Task A_valid_double_submit_pair_still_completes_the_mutation()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "Envelope Happy Path");
        var workspaceId = auth.GetProperty("personalWorkspace").GetProperty("id").GetString();
        var token = await GetCsrfAsync(client);

        var response = await SendWithToken(client, HttpMethod.Post,
            $"/api/workspaces/{workspaceId}/projects", token,
            new { name = "Envelope Project", description = "1", type = "Business" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = await ReadJson(response);
        Assert.Equal("Envelope Project", project.GetProperty("name").GetString());
    }

    private static async Task<HttpResponseMessage> SendWithoutToken(
        HttpClient client, string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendWithToken(
        HttpClient client, HttpMethod method, string path, string token, object payload)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static void AssertCsrfEnvelope(JsonElement body)
    {
        Assert.Equal(CsrfFailureCode,
            body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Request validation failed.",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var issued = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return issued.GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> RegisterAsync(HttpClient client, string displayName)
    {
        var token = await GetCsrfAsync(client);
        var response = await SendWithToken(client, HttpMethod.Post, "/api/auth/register", token,
            new
            {
                displayName,
                email = $"{displayName.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}@example.com",
                password = Password,
                preferredLanguage = "en",
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJson(response);
    }
}
