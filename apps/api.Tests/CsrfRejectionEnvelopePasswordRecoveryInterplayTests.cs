using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Interplay between the stable CSRF rejection envelope (<c>AntiforgeryFailureEnvelopeFilter</c>)
/// and the password-recovery lane, which extends <c>AntiforgeryValidationMiddleware</c>'s
/// exact-path anonymous endpoint list with <c>/api/auth/password/forgot</c> and
/// <c>/api/auth/password/reset</c> while leaving both controller actions decorated with
/// <c>[ValidateAntiForgeryToken]</c>.
///
/// The two lanes must compose: the envelope code must never depend on which enforcement
/// layer observed the request, and adding a middleware path entry must not change the
/// rejection body any client already parses. The recovery endpoints themselves live on
/// that lane's branch, so this suite drives the composition through synthetic, test-host-only
/// endpoints that replicate their exact antiforgery shape (anonymous POST, controller
/// attribute, middleware-adjacent path). No real recovery logic, token, provider, secret,
/// or customer data is involved.
///
/// Security invariants pinned here, per the release gate for the recovery lane:
/// rejection stays fail-closed (the action never runs without a valid double-submit pair),
/// the envelope is byte-identical across enforcement layers, and no antiforgery
/// configuration or token handling is weakened to make the envelopes agree.
/// </summary>
public sealed class CsrfRejectionEnvelopePasswordRecoveryInterplayTests : IClassFixture<CsrfRejectionEnvelopePasswordRecoveryInterplayTests.RecoveryLaneFactory>
{
    private const string CsrfFailureCode = "CSRF_VALIDATION_FAILED";
    private const string Password = "StrongPassword!123";

    // Mirrors the recovery lane's controller shape: anonymous POSTs guarded only by the
    // MVC [ValidateAntiForgeryToken] attribute. Paths stay under the test-only namespace
    // so they can never collide with a future production route.
    private const string ForgotPath = "/api/auth/test-recovery/forgot";
    private const string ResetPath = "/api/auth/test-recovery/reset";
    private const string ForgotVariantPath = "/API/Auth/Test-Recovery/Forgot/";

    private readonly RecoveryLaneFactory factory;

    public CsrfRejectionEnvelopePasswordRecoveryInterplayTests(RecoveryLaneFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// A recovery-lane-shaped anonymous antiforgery POST pair. Deliberately does NOT
    /// opt into <c>[IgnoreAntiforgeryToken]</c> and does not touch any shared antiforgery
    /// configuration, so a rejection reaches MVC's <c>AntiforgeryValidationFailedResult</c>
    /// exactly as a stale token on <c>/api/auth/password/forgot</c> would when the request
    /// spelling skips the middleware's exact-path list.
    /// </summary>
    [ApiController]
    [Route("api/auth/test-recovery")]
    public sealed class SyntheticRecoveryLaneController : ControllerBase
    {
        [HttpPost("forgot")]
        [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
        public IActionResult Forgot() => Ok(new { lane = "forgot", executed = true });

        [HttpPost("reset")]
        [Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken]
        public IActionResult Reset() => Ok(new { lane = "reset", executed = true });
    }

    // The test assembly is not an MVC application part, so the synthetic controller is
    // contributed by type directly. The default provider's conventions exclude it (a
    // nested class is not scanned), so no duplicate registration is possible.
    private sealed class RecoveryControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            if (!feature.Controllers.Any(c => c.AsType() == typeof(SyntheticRecoveryLaneController)))
            {
                feature.Controllers.Add(typeof(SyntheticRecoveryLaneController).GetTypeInfo());
            }
        }
    }

    /// <summary>
    /// The shared API host plus the synthetic recovery-lane controller, registered only
    /// through the application part manager of the test host. Production routing and the
    /// production middleware endpoint list are untouched by this fixture.
    /// </summary>
    public sealed class RecoveryLaneFactory : TaslimApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // AddMvcCore registers the ApplicationPartManager as a concrete instance and
            // collects its controller feature lazily, on the first action-descriptor
            // build. Appending the provider to that registered instance therefore still
            // contributes the synthetic controller, while production controller
            // discovery is untouched: the default provider rejects this type because it
            // is a nested class, so it can only arrive through this provider.
            builder.ConfigureTestServices(services =>
            {
                var partManager = services.LastOrDefault(d => d.ServiceType == typeof(ApplicationPartManager))
                    ?.ImplementationInstance as ApplicationPartManager
                    ?? throw new InvalidOperationException("MVC is not registered in the test host.");
                if (!partManager.FeatureProviders.OfType<RecoveryControllerFeatureProvider>().Any())
                {
                    partManager.FeatureProviders.Add(new RecoveryControllerFeatureProvider());
                }
            });
        }
    }

    [Theory]
    [InlineData(ForgotPath)]
    [InlineData(ResetPath)]
    [InlineData(ForgotVariantPath)] // trailing separator and case: routed by MVC, skipped by the middleware
    public async Task Recovery_lane_shaped_rejection_returns_the_stable_csrf_envelope(string path)
    {
        using var client = factory.CreateClient();
        using var response = await PostWithoutToken(client, path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(CsrfFailureCode, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Request validation failed.", body.GetProperty("error").GetProperty("message").GetString());

        // Fail closed: the rejection contract must not be softened into a pass-through.
        Assert.DoesNotContain("executed", body.ToString());
    }

    [Fact]
    public async Task Recovery_lane_shaped_action_still_runs_for_a_valid_double_submit_pair()
    {
        using var client = factory.CreateClient();
        var token = await GetCsrfAsync(client);

        foreach (var (path, lane) in new[] { (ForgotPath, "forgot"), (ResetPath, "reset") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("X-CSRF-TOKEN", token);
            using var response = await client.SendAsync(request);

            // The envelope filter must not swallow successful results: enforcement stays
            // attribute-based, and a valid pair still reaches the action.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadJson(response);
            Assert.True(body.GetProperty("executed").GetBoolean());
            Assert.Equal(lane, body.GetProperty("lane").GetString());
        }
    }

    [Fact]
    public async Task A_tampered_request_token_on_the_recovery_lane_keeps_the_same_envelope()
    {
        using var client = factory.CreateClient();
        var token = await GetCsrfAsync(client);

        // Cookie issued, request header forged: validation must stay rejected and the
        // rejection must not degrade into a token-detail-bearing error.
        using var request = new HttpRequestMessage(HttpMethod.Post, ForgotPath)
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("X-CSRF-TOKEN", token + "tampered");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(CsrfFailureCode, body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("type", out _), "The rejection must not expose a Problem Details type URI.");
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Middleware_layer_and_filter_layer_rejections_are_byte_identical()
    {
        using var client = factory.CreateClient();

        // Canonical login: rejected by AntiforgeryValidationMiddleware (exact-path list).
        using var middlewareRejection = await PostWithoutToken(client, "/api/auth/login",
            new { email = "interplay-canonical@example.com", password = Password });
        // Recovery-lane spelling: skipped by that list, rejected by the MVC filter.
        using var filterRejection = await PostWithoutToken(client, ForgotPath);

        Assert.Equal(HttpStatusCode.BadRequest, middlewareRejection.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, filterRejection.StatusCode);

        // Document identity is what lets the recovery lane extend the middleware list with
        // zero contract divergence for the client: whichever layer answers, the parsed
        // envelope is the same document.
        var middlewareBody = await ReadJson(middlewareRejection);
        var filterBody = await ReadJson(filterRejection);
        Assert.Equal(CsrfFailureCode, middlewareBody.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(CsrfFailureCode, filterBody.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(middlewareBody.GetRawText(), filterBody.GetRawText());
        foreach (var rejection in new[] { middlewareRejection, filterRejection })
        {
            Assert.Equal("application/json", rejection.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public void The_two_lanes_touch_disjoint_enforcement_surfaces()
    {
        // Source guard for the composition contract: the envelope filter runs ahead of
        // MVC's client-error Problem Details mapping (-2000) so the concrete rejection
        // type is erased before any other filter can observe it...
        Assert.Equal(-2100, Taslim.Api.Infrastructure.AntiforgeryFailureEnvelopeFilter.EnvelopeOrder);

        // ...and the middleware keeps naming its anonymous endpoints by an exact-path
        // pattern rather than a prefix or normalizer. The recovery lane therefore adds
        // its endpoints purely by extending that list, which cannot remove coverage the
        // filter already provides: any spelling the list misses still receives the
        // identical envelope from the MVC layer.
        var middlewareSource = ReadEmbeddedlessSource("apps/api/Infrastructure/AntiforgeryValidationMiddleware.cs");
        Assert.Contains("context.Request.Path.Value is", middlewareSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StartsWithSegments(\"/api/auth", middlewareSource, StringComparison.Ordinal);
    }

    private static string ReadEmbeddedlessSource(string repoRelativePath)
    {
        // Tests execute from the api.Tests output directory under the repository root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "apps/api/Program.cs")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory), repoRelativePath);
        return File.ReadAllText(path);
    }

    private static async Task<HttpResponseMessage> PostWithoutToken(HttpClient client, string path, object? payload = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload ?? new { }),
        };
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var issued = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return issued.GetProperty("token").GetString()!;
    }
}
