using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Persistence;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA boundary probe for the stable CSRF rejection envelope of PR #1039.
///
/// The lane pins the rejection contract for POST: the anonymous authentication endpoints and
/// their routed spellings, the password-recovery interplay, and the Accept-negotiation
/// dimension owned by the separate draft lane #1095. None of those tests ever issues an unsafe
/// method other than POST. Enforcement is not POST-only though:
/// <see cref="AntiforgeryValidationMiddleware"/> treats POST, PUT, PATCH and DELETE alike, so
/// the contract extends over three verbs no existing assertion exercises, and it has to hold
/// for requests whose request <c>Content-Type</c> the endpoint cannot bind and whose
/// <c>Accept</c> does not describe the answer.
///
/// Those two mismatches are exactly the ways a request can fail for a reason that is not
/// antiforgery: an unbindable body type is normally 415 Unsupported Media Type, an
/// unsatisfiable Accept spelling is normally 406 Not Acceptable, and a verb with no matching
/// action is normally 405. If any of those ever won first, the caller would receive a document
/// with no <c>error.code</c> -- the failure mode this envelope exists to remove -- and a forged
/// mutation would read as a client bug rather than as a refusal.
///
/// Pinned here, for PUT, PATCH and DELETE:
///   1. no bypass: a refused unsafe-method request always answers 400 with the
///      <c>CSRF_VALIDATION_FAILED</c> envelope whatever its Content-Type or Accept, and that
///      document is byte-identical to the one the middleware writes for POST, so the client's
///      single refresh-and-retry path is reachable from every verb;
///   2. refusal precedes routing and binding: a 400 with the CSRF code, rather than a 405 or
///      415, is what an unmatched verb or an unbindable body type answers, proving the
///      antiforgery layer ran first;
///   3. nothing persists: the forged rename never lands, and the same route with a valid pair
///      does rename it, which makes that claim about antiforgery rather than about a route
///      that cannot write;
///   4. no over-blocking: a valid double-submit pair is never answered with the CSRF code, so
///      the observed 400s cannot be reproduced by a server that refuses everything;
///   5. no leakage: the refusal never carries the antiforgery cookie name, the session token,
///      the submitted credential, or any Problem Details member.
///
/// Bounded on purpose. One synthetic session serves the class and the middleware reference
/// document is read once: xunit builds a new instance of the test class per case, so a session
/// created in the constructor would register once per case, exhaust the shared authentication
/// rate-limit partition, and make unrelated legs fail on 429 instead of on the contract.
///
/// Fully synthetic otherwise -- in-memory SQLite, one throwaway account, generated
/// identifiers, no provider call, no network egress, no real account, no secret material.
/// </summary>
public sealed class CsrfRejectionEnvelopeUnsafeMethodBoundaryTests
    : IClassFixture<CsrfRejectionEnvelopeUnsafeMethodBoundaryTests.BoundarySession>
{
    private const string Password = "StrongPassword!123";
    private const string CsrfFailureCode = "CSRF_VALIDATION_FAILED";
    private const string CsrfFailureMessage = "Request validation failed.";
    private const string ProjectName = "Unsafe Boundary Project";

    private static readonly string[] UnsafeVerbs = ["PATCH", "PUT", "DELETE"];

    // Body types no JSON-bound action can deserialize. With a valid token an unbindable body
    // reaches the binder and answers 415, so an observed 400 proves refusal came first.
    private static readonly string?[] RequestShapes =
        ["application/x-www-form-urlencoded", "text/plain", null];

    private static readonly string?[] AcceptSpellings =
        [null, "text/html", "application/json", "*/*", "text/html,application/xhtml+xml,*/*"];

    private readonly BoundarySession session;

    public CsrfRejectionEnvelopeUnsafeMethodBoundaryTests(BoundarySession session)
    {
        this.session = session;
    }

    /// <summary>
    /// One authenticated synthetic session for the whole class. The CSRF token is read after
    /// registration, because registration rotates the antiforgery cookie and a pre-login token
    /// is silently dead, which would turn every control leg into a false pass.
    /// </summary>
    public sealed class BoundarySession : IDisposable
    {
        private readonly TaslimApiFactory factory;

        // The session owns its host rather than borrowing the shared class fixture, so this
        // file stays isolated: it cannot perturb, or be perturbed by, the other CSRF lanes.
        public BoundarySession()
        {
            factory = new TaslimApiFactory();
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();

            Client = factory.CreateClient();
            var issued = GetCsrfAsync().GetAwaiter().GetResult();
            using var registration = RegisterAsync(issued).GetAwaiter().GetResult();
            Token = GetCsrfAsync().GetAwaiter().GetResult();
        }

        public HttpClient Client { get; }

        public string Token { get; }

        /// <summary>
        /// The middleware document, captured once: the POST refusal produced by
        /// <see cref="AntiforgeryValidationMiddleware"/> on its exact-path list, which is the
        /// path this lane must not disturb.
        /// </summary>
        public string? MiddlewareReference { get; set; }

        public void Dispose()
        {
            Client.Dispose();
            factory.Dispose();
        }

        public string ReadMiddlewareReference()
        {
            if (MiddlewareReference is not null)
            {
                return MiddlewareReference;
            }

            using var reference = Client.SendAsync(new HttpRequestMessage(
                HttpMethod.Post, "/api/auth/login")
            {
                Content = Json(new { email = "unsafe-boundary-reference@example.com", password = Password }),
            }).GetAwaiter().GetResult();

            Assert.Equal(HttpStatusCode.BadRequest, reference.StatusCode);
            MiddlewareReference = reference.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains(CsrfFailureCode, MiddlewareReference, StringComparison.Ordinal);
            return MiddlewareReference;
        }

        public async Task<string> CreateProjectAsync(string name)
        {
            var auth = await Client.GetFromJsonAsync<JsonElement>("/api/auth/me");
            var workspaceId = auth.GetProperty("personalWorkspace").GetProperty("id").GetString()!;

            using var created = await Client.SendAsync(new HttpRequestMessage(
                HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects")
            {
                Content = Json(new { name, description = "probe", type = "Business" }),
                Headers = { { "X-CSRF-TOKEN", Token } },
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var project = await created.Content.ReadFromJsonAsync<JsonElement>();
            return project.GetProperty("id").GetString()!;
        }

        private async Task<HttpResponseMessage> RegisterAsync(string issuedToken)
        {
            var response = await Client.SendAsync(new HttpRequestMessage(
                HttpMethod.Post, "/api/auth/register")
            {
                Content = Json(new
                {
                    displayName = "Unsafe Boundary Owner",
                    email = $"unsafe-boundary-{Guid.NewGuid():N}@example.com",
                    password = Password,
                    preferredLanguage = "en",
                }),
                Headers = { { "X-CSRF-TOKEN", issuedToken } },
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response;
        }

        private async Task<string> GetCsrfAsync()
        {
            var issued = await Client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
            return issued.GetProperty("token").GetString()!;
        }
    }

    public static TheoryData<string, string?, string?> RefusalMatrix
    {
        get
        {
            var data = new TheoryData<string, string?, string?>();
            foreach (var verb in UnsafeVerbs)
            foreach (var shape in RequestShapes)
            foreach (var accept in AcceptSpellings)
            {
                data.Add(verb, shape, accept);
            }
            return data;
        }
    }

    public static TheoryData<string?> RequestShapeCases => new()
    {
        "application/x-www-form-urlencoded",
        "text/plain",
        null,
    };

    public static TheoryData<string, string?> UnsafeMethodShapes
    {
        get
        {
            var data = new TheoryData<string, string?>();
            foreach (var verb in UnsafeVerbs)
            foreach (var shape in RequestShapes)
            {
                data.Add(verb, shape);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RefusalMatrix))]
    public async Task Refused_unsafe_method_returns_the_stable_csrf_envelope(
        string verb, string? requestContentType, string? accept)
    {
        using var response = await SendAsync(verb, MissingMemoryPath(),
            requestContentType, token: null, accept: accept);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJson(response);
        Assert.Equal(CsrfFailureCode, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(CsrfFailureMessage, body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [MemberData(nameof(UnsafeMethodShapes))]
    public async Task Every_unsafe_verb_refusal_is_byte_identical_to_the_middleware_document(
        string verb, string? requestContentType)
    {
        var referenceText = session.ReadMiddlewareReference();

        using var response = await SendAsync(verb, MissingMemoryPath(),
            requestContentType, token: null, accept: "text/html");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Byte identity, not code equality alone: a refusal matching only on error.code but
        // differing in shape would still break the client's recovery path.
        Assert.Equal(referenceText, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(UnsafeMethodShapes))]
    public async Task A_valid_double_submit_pair_is_never_answered_with_the_csrf_code(
        string verb, string? requestContentType)
    {
        // Control leg. A real token clears the enforcement layer even when the body type
        // cannot be bound, the verb has no matching action or the row does not exist, so the
        // answer is a routing, binding or not-found failure and never the CSRF code. Without
        // it, a server that refused everything would satisfy every other assertion here.
        using var response = await SendAsync(verb, MissingMemoryPath(),
            requestContentType, token: session.Token, accept: "text/html");

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(CsrfFailureCode, raw, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(RequestShapeCases))]
    public async Task A_refused_rename_never_persists_and_the_owner_rename_does(
        string? requestContentType)
    {
        // Rename is a PATCH route; each leg forges it with the mismatched request shape named
        // by the case, so the refusal is exercised under every shape while the write path
        // stays the real one.
        var projectPath = $"/api/projects/{await session.CreateProjectAsync(ProjectName)}";

        // The row exists, so an accepted PATCH would rename it. Reading it back is executed
        // evidence that the refusal preceded the action, not merely that a 400 came back.
        using var forgery = await SendAsync("PATCH", projectPath, requestContentType,
            token: null, accept: "text/html",
            body: JsonSerializer.Serialize(new { name = "Renamed By Forgery", type = "Business" }));
        Assert.Equal(HttpStatusCode.BadRequest, forgery.StatusCode);

        var after = await session.Client.GetFromJsonAsync<JsonElement>(projectPath);
        Assert.Equal(ProjectName, after.GetProperty("name").GetString());

        // The same route with a valid pair does rename it, so the pin above is about the
        // antiforgery refusal rather than about a route that cannot write.
        using var owner = await SendAsync("PATCH", projectPath, "application/json",
            token: session.Token, accept: "application/json",
            body: JsonSerializer.Serialize(new { name = "Renamed By Owner", type = "Business" }));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        var renamed = await session.Client.GetFromJsonAsync<JsonElement>(projectPath);
        Assert.Equal("Renamed By Owner", renamed.GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(UnsafeMethodShapes))]
    public async Task Refusal_leaks_no_token_cookie_credential_or_problem_detail(
        string verb, string? requestContentType)
    {
        // A stale-but-well-formed token is the spelling most likely to surface framework
        // detail, so the leak pin runs against it rather than against an absent token.
        using var response = await SendAsync(verb, MissingMemoryPath(), requestContentType,
            token: $"stale-{session.Token}", accept: "text/html,application/xhtml+xml,*/*");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("taslim.csrf", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.Token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, raw, StringComparison.Ordinal);
        var body = await ReadJson(response);
        Assert.False(body.TryGetProperty("type", out _), "No Problem Details type URI.");
        Assert.False(body.TryGetProperty("title", out _), "No Problem Details title.");
        Assert.False(body.TryGetProperty("detail", out _), "No Problem Details detail.");
        Assert.False(body.TryGetProperty("stackTrace", out _), "No exception detail.");
    }

    private static string MissingMemoryPath() => $"/api/memories/{Guid.NewGuid()}";

    private static StringContent Json(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(string verb, string path,
        string? requestContentType, string? token, string? accept, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(verb), path);
        var payload = body ?? "title=x";

        if (requestContentType is not null)
        {
            request.Content = new StringContent(payload, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(requestContentType);
        }
        else if (verb != "DELETE")
        {
            // A body with no declared media type: no binder can be negotiated for it, which
            // is the other half of the mismatch that antiforgery must not care about.
            request.Content = new StringContent(payload, Encoding.UTF8);
            request.Content.Headers.ContentType = null;
        }

        if (accept is not null)
        {
            Assert.True(request.Headers.TryAddWithoutValidation("Accept", accept));
        }
        if (token is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", token);
        }

        return await session.Client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();
}
