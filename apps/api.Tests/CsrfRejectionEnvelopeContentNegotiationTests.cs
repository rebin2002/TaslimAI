using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Pins the Accept-header dimension of the normalized CSRF rejection envelope introduced
/// by the antiforgery-envelope lane (<c>AntiforgeryFailureEnvelopeFilter</c> plus
/// <c>AntiforgeryValidationMiddleware</c>): the machine-readable
/// <c>CSRF_VALIDATION_FAILED</c> document must reach every kind of caller unchanged.
///
/// The existing lane suites pin the envelope per path spelling and per enforcement layer,
/// but every request they issue carries no explicit <c>Accept</c> header, so they prove the
/// contract only for the default-negotiation case. The two layers normalize through
/// genuinely different mechanisms: the middleware writes the JSON document directly to the
/// response, while the MVC filter replaces the antiforgery result with an object result
/// that declares <c>application/json</c> and therefore travels through output-formatter
/// content negotiation. A browser form post advertises <c>Accept: text/html</c> and a
/// <c>fetch</c> call advertises <c>*/*</c>: if negotiation could ever steer the MVC layer
/// to a 406, an empty body, or an HTML Problem Details page for a <c>text/html</c>
/// caller, the web client would lose the <c>error.code</c> it depends on and a recoverable
/// rejection would again become a hard failure -- but only for HTML callers, invisible to
/// the header-free suites.
///
/// This suite closes exactly that blind spot and nothing else:
///   1. absent, <c>application/json</c>, <c>text/html</c>, <c>*/*</c> and a browser-style
///      <c>text/html,application/xhtml+xml,*/*</c> Accept spellings all receive the same
///      status, the same <c>application/json</c> response media type, and a byte-identical
///      envelope body, on the middleware-routed and MVC-routed path spellings alike;
///   2. the rejection body stays free of internal exception / Problem Details / token
///      material under every Accept spelling;
///   3. a refused mutation never persists (the rejection is observed before any action
///      runs) and a valid double-submit pair is not mutated into a rejection by the
///      envelope normalization when the caller advertises <c>text/html</c>.
///
/// All requests are synthetic against the in-memory test host: no live endpoint, no
/// browser account, no provider, payment or billing path, and no customer data.
/// </summary>
public sealed class CsrfRejectionEnvelopeContentNegotiationTests : IClassFixture<TaslimApiFactory>
{
    private const string Password = "StrongPassword!123";
    private const string CsrfFailureCode = "CSRF_VALIDATION_FAILED";
    private const string CsrfFailureMessage = "Request validation failed.";
    private const string ExpectedEnvelope = """{"error":{"code":"CSRF_VALIDATION_FAILED","message":"Request validation failed."}}""";
    private const string MiddlewareSpelling = "/api/auth/login";   // in the middleware exact-path list
    private const string MvcLayerSpelling = "/api/auth/login/";    // routed by MVC, missed by the list

    // Accept spellings a real caller uses: absent (bare HTTP client), explicit JSON (the
    // web API client), HTML (browser form post), the fetch wildcard, and the full
    // browser-sent list. null means "send no Accept header at all".
    private static readonly string?[] AcceptSpellings =
    [
        null,
        "application/json",
        "text/html",
        "*/*",
        "text/html,application/xhtml+xml,*/*",
    ];

    private readonly TaslimApiFactory factory;

    public CsrfRejectionEnvelopeContentNegotiationTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData(MiddlewareSpelling)]
    [InlineData(MvcLayerSpelling)]
    public async Task Rejection_envelope_is_identical_across_every_accept_spelling(string path)
    {
        using var client = factory.CreateClient();

        foreach (var accept in AcceptSpellings)
        {
            using var response = await PostWithoutToken(client, path,
                new { email = "negotiation-probe@example.com", password = Password }, accept);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            // One pinned document for every spelling: byte-identity across the Accept
            // matrix is implied by equality with this exact literal.
            Assert.Equal(ExpectedEnvelope, await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("*/*")]
    public async Task Middleware_layer_and_filter_layer_rejections_are_byte_identical_per_accept(string accept)
    {
        using var client = factory.CreateClient();
        using var middlewareRejection = await PostWithoutToken(client, MiddlewareSpelling,
            new { email = "layer-cross@example.com", password = Password }, accept);
        using var filterRejection = await PostWithoutToken(client, MvcLayerSpelling,
            new { email = "layer-cross@example.com", password = Password }, accept);

        // The two layers normalize through different mechanisms (direct response write vs
        // negotiated object result); no Accept spelling may split the contract they serve.
        Assert.Equal(HttpStatusCode.BadRequest, middlewareRejection.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, filterRejection.StatusCode);
        Assert.Equal(
            middlewareRejection.Content.Headers.ContentType?.MediaType,
            filterRejection.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            await middlewareRejection.Content.ReadAsStringAsync(),
            await filterRejection.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData("text/html,application/xhtml+xml,*/*")]
    public async Task Rejection_carries_no_internal_detail_under_any_accept(string accept)
    {
        using var client = factory.CreateClient();
        using var response = await PostWithoutToken(client, MvcLayerSpelling,
            new { email = "leak-probe@example.com", password = Password }, accept);
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // Exactly the envelope shape: nothing else may ride along.
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("error", out var error));
        Assert.Equal(new[] { "error" }, root.EnumerateObject().Select(member => member.Name).ToArray());
        Assert.Equal(new[] { "code", "message" }, error.EnumerateObject().Select(member => member.Name).ToArray());
        Assert.Equal(CsrfFailureCode, error.GetProperty("code").GetString());
        Assert.Equal(CsrfFailureMessage, error.GetProperty("message").GetString());

        // Problem Details members are the generic-body fingerprints the fix must erase,
        // whatever the caller advertised.
        foreach (var forbidden in new[] { "type", "title", "status", "detail", "instance", "traceId", "errors" })
        {
            Assert.DoesNotContain($"\"{forbidden}\"", body, StringComparison.OrdinalIgnoreCase);
        }
        // No internal exception or stack material, and no antiforgery configuration names
        // or submitted credentials echoed back: the refusal text is fixed.
        foreach (var forbidden in new[] { "exception", "stack", "at Taslim", "Microsoft.", "taslim.csrf", "taslim.auth", Password })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Html_accepting_refused_mutation_never_persists_and_never_leaks()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "Negotiation Refusal");
        var workspaceId = auth.GetProperty("personalWorkspace").GetProperty("id").GetString();
        var projectName = "Must Never Exist";

        // A browser form post (Accept: text/html) forging the double-submit pair: refused
        // at the antiforgery boundary, before any action or persistence runs.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects")
        {
            Content = JsonContent.Create(new { name = projectName, type = "Business" }),
        };
        Assert.True(request.Headers.TryAddWithoutValidation("Accept", "text/html"));
        request.Headers.Add("X-CSRF-TOKEN", "forged-not-the-cookie-pair");
        using var rejection = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, rejection.StatusCode);
        Assert.Equal("application/json", rejection.Content.Headers.ContentType?.MediaType);
        var body = await rejection.Content.ReadAsStringAsync();
        Assert.Equal(ExpectedEnvelope, body);
        Assert.DoesNotContain(projectName, body, StringComparison.Ordinal);
        Assert.DoesNotContain("forged-not-the-cookie-pair", body, StringComparison.Ordinal);

        // No mutation: the refused project was not created.
        using var list = await client.GetAsync($"/api/workspaces/{workspaceId}/projects");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var projects = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(projects.EnumerateArray(), p =>
            p.GetProperty("name").GetString() == projectName);
    }

    [Fact]
    public async Task Html_accepting_valid_pair_still_completes_and_is_not_rewritten_as_rejection()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "Negotiation Happy Path");
        var workspaceId = auth.GetProperty("personalWorkspace").GetProperty("id").GetString();
        var token = await GetCsrfAsync(client);

        // The envelope filter must stay invisible to accepted traffic: an HTML-accepting
        // caller with a valid double-submit pair still gets its Created result.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects")
        {
            Content = JsonContent.Create(new { name = "Accepted Under Html", description = "1", type = "Business" }),
        };
        Assert.True(request.Headers.TryAddWithoutValidation("Accept", "text/html"));
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Accepted Under Html", project.GetProperty("name").GetString());
        Assert.DoesNotContain(CsrfFailureCode, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostWithoutToken(
        HttpClient client, string path, object payload, string? accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };
        // Raw spelling, exactly as a browser or a bare client sends it, including the
        // comma-separated browser list that the typed header parser rejects on purpose.
        Assert.True(accept is null || request.Headers.TryAddWithoutValidation("Accept", accept));
        return await client.SendAsync(request);
    }

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var issued = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return issued.GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> RegisterAsync(HttpClient client, string displayName)
    {
        var token = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName,
                email = $"{displayName.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}@example.com",
                password = Password,
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
