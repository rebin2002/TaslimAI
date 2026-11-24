using System.Net;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class AcademicHttpGatewayTests
{
    private static Uri OkEndpoint(string path = "works") => new($"https://api.openalex.org/{path}", UriKind.Absolute);

    private static (AcademicHttpGateway gateway, ScriptedHandler handler) Create(
        Func<HttpRequestMessage, int, HttpResponseMessage> script,
        Action<AcademicSourceDiscoveryOptions>? configure = null)
    {
        var handler = new ScriptedHandler(script);
        var options = AcademicFixture.Defaults(configure);
        return (new AcademicHttpGateway(AcademicFixture.Client(handler), options), handler);
    }

    [Fact]
    public async Task Reads_json_success_payloads()
    {
        var (gateway, _) = Create((_, _) => AcademicFixture.Json("""{"ok":true,"count":7}"""));
        var response = await gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None);
        Assert.True(response.IsSuccess);
        Assert.Equal(7, response.Root.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Off_allow_list_endpoints_are_blocked_before_any_traffic()
    {
        var (gateway, handler) = Create((_, _) => AcademicFixture.Json("{}"));
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() =>
            gateway.GetJsonAsync(new Uri("http://169.254.169.254/latest/meta-data/iam/"), CancellationToken.None));
        Assert.Equal("blocked_endpoint", exception.Details?.FailureCategory);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Retries_rate_limit_with_retry_after_then_succeeds()
    {
        var (gateway, handler) = Create((_, number) =>
        {
            if (number == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(0));
                return response;
            }
            return AcademicFixture.Json("""{"meta":{}}""");
        });
        var response = await gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None);
        Assert.True(response.IsSuccess);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Retry_after_ceiling_is_applied_and_attempts_are_bounded()
    {
        var (gateway, handler) = Create(
            (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            options => { options.MaxAttempts = 2; options.MaxRetryDelaySeconds = 0; options.BackoffBaseMilliseconds = 0; });
        await Assert.ThrowsAsync<AcademicSourceUnavailableException>(() => gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Redirects_are_revalidated_against_the_allow_list()
    {
        var (gateway, handler) = Create((_, number) => number switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://internal.corp.example/steal") } },
            _ => AcademicFixture.Json("{}"),
        });
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None));
        Assert.Equal("blocked_endpoint", exception.Details?.FailureCategory);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Allowed_redirect_chain_is_followed_up_to_the_hop_budget()
    {
        var (gateway, handler) = Create((_, number) => number switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Moved) { Headers = { Location = new Uri("https://api.openalex.org/works-v2") } },
            2 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.openalex.org/works-v3") } },
            _ => AcademicFixture.Json("""{"hop":"final"}"""),
        }, options => options.MaxRedirectHops = 2);
        var response = await gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None);
        Assert.Equal("final", response.Root.GetProperty("hop").GetString());
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Redirect_loops_hit_the_hop_ceiling()
    {
        var (gateway, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://api.openalex.org/works") },
        }, options => options.MaxRedirectHops = 1);
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None));
        Assert.Equal("too_many_redirects", exception.Details?.FailureCategory);
    }

    // Transport policy. The endpoint allow list says WHICH host may be spoken to; these tests pin
    // that it may only be spoken to over TLS, on every hop, because a scholarly request target is
    // entirely query string — the reader's topic in `q=`, and for Google Books the deployment's
    // configured key in `key=`. An https → http hop on the same host is the shape that used to read
    // as "same authority" and be followed, so each case below asserts both the refusal and the
    // request count: the plaintext address must never have been dialed.
    [Fact]
    public async Task Https_to_http_redirect_on_the_same_host_is_refused_and_never_requested()
    {
        var (gateway, handler) = Create((_, number) => number switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Moved)
            {
                Headers = { Location = new Uri("http://api.openalex.org/works?q=vaccine+policy&key=synthetic-provider-key") },
            },
            _ => AcademicFixture.Json("""{"plaintext":"served"}"""),
        });
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(
            new Uri("https://api.openalex.org/works?q=vaccine+policy&key=synthetic-provider-key"), CancellationToken.None));
        Assert.Equal("insecure_transport", exception.Details?.FailureCategory);
        Assert.Equal(1, handler.RequestCount);
        Assert.All(handler.Requests, request => Assert.False(
            request.RequestUri!.ToString().StartsWith("http://", StringComparison.Ordinal),
            $"a plaintext hop was requested: {request.RequestUri}"));
    }

    [Fact]
    public async Task The_plaintext_hop_is_refused_even_when_the_test_switch_is_on()
    {
        // The downgrade rule is not the configuration rule: switching plaintext on for an injected
        // test transport may not turn a TLS conversation into a cleartext one mid-flight.
        var (gateway, handler) = Create((_, number) => number switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://api.openalex.org/works") },
            },
            _ => AcademicFixture.Json("{}"),
        }, options => options.AllowInsecureProviderEndpoints = true);
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(
            OkEndpoint(), CancellationToken.None));
        Assert.Equal("insecure_transport", exception.Details?.FailureCategory);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Plaintext_provider_endpoints_are_refused_before_any_traffic()
    {
        var (gateway, handler) = Create((_, _) => AcademicFixture.Json("{}"));
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(
            new Uri("http://api.openalex.org/works"), CancellationToken.None));
        Assert.Equal("insecure_transport", exception.Details?.FailureCategory);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task The_host_question_and_the_transport_question_stay_in_different_places()
    {
        // Two components, two decisions, and the order between them is the point. The allow-list
        // classifier is shared with the rest of the product and answers only "is this host one we
        // talk to" — it says nothing about TLS, and changing that shared answer is not this slice's
        // call. The gateway owns the transport, so an allow-listed host is still refused when the
        // request would go out in the clear. Pinned here so a future reader cannot conclude from the
        // classifier alone that plaintext scholarly traffic was approved.
        var options = AcademicFixture.Defaults();
        Assert.True(AcademicSourceUrlGuard.IsAllowedEndpoint(
            new Uri("http://api.openalex.org/works"), options.AllowedEndpointHosts()));
        var (gateway, handler) = Create((_, _) => AcademicFixture.Json("{}"));
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(
            new Uri("http://api.openalex.org/works"), CancellationToken.None));
        Assert.Equal("insecure_transport", exception.Details?.FailureCategory);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task An_injected_test_transport_serves_plaintext_without_credentials()
    {
        var (gateway, handler) = Create((_, _) => AcademicFixture.Json("""{"plain":true}"""),
            options => options.AllowInsecureProviderEndpoints = true);
        var response = await gateway.GetJsonAsync(new Uri("http://api.openalex.org/works?q=topic"), CancellationToken.None);
        Assert.True(response.IsSuccess);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task The_test_switch_still_refuses_a_credential_in_the_clear()
    {
        var (gateway, handler) = Create((_, _) => AcademicFixture.Json("{}"),
            options => options.AllowInsecureProviderEndpoints = true);
        var exception = await Assert.ThrowsAsync<AcademicSourceRejectedException>(() => gateway.GetJsonAsync(
            new Uri("http://api.openalex.org/works?key=synthetic-provider-key"), CancellationToken.None));
        Assert.Equal("insecure_transport", exception.Details?.FailureCategory);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task A_same_host_upgrade_into_tls_keeps_its_query_and_is_followed()
    {
        var (gateway, handler) = Create((_, number) => number switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Moved)
            {
                Headers = { Location = new Uri("https://api.openalex.org/works?q=topic") },
            },
            _ => AcademicFixture.Json("""{"secure":true}"""),
        }, options => options.AllowInsecureProviderEndpoints = true);
        var response = await gateway.GetJsonAsync(new Uri("http://api.openalex.org/works?q=topic"), CancellationToken.None);
        Assert.True(response.IsSuccess);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("https", handler.Requests[1].RequestUri!.Scheme);
        Assert.Contains("q=topic", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_payloads_are_refused_not_buffered()
    {
        var big = new string('x', 5_000_000);
        var (gateway, _) = Create((_, _) => AcademicFixture.Json($"\"{big}\""), options => options.MaxResponseBytes = 8_192);
        var exception = await Assert.ThrowsAsync<AcademicSourceMalformedException>(() => gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None));
        Assert.Equal("payload_too_large", exception.Details?.FailureCategory);
    }

    [Fact]
    public async Task Non_json_content_types_are_rejected()
    {
        var (gateway, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>", System.Text.Encoding.UTF8, "text/html"),
        });
        var exception = await Assert.ThrowsAsync<AcademicSourceMalformedException>(() => gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None));
        Assert.Equal("unexpected_content_type", exception.Details?.FailureCategory);
    }

    [Fact]
    public async Task Json_media_type_variants_are_accepted()
    {
        foreach (var mediaType in new[] { "application/json", "application/hal+json", "text/json" })
        {
            var (gateway, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, mediaType),
            });
            var result = await gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None);
            Assert.True(result.IsSuccess, mediaType);
        }

        var (charsetGateway, _) = Create((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            return response;
        });
        var charsetResult = await charsetGateway.GetJsonAsync(OkEndpoint(), CancellationToken.None);
        Assert.True(charsetResult.IsSuccess);
    }

    [Fact]
    public async Task Non_json_media_type_is_rejected_safely()
    {
        var (gateway, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain"),
        });
        var exception = await Assert.ThrowsAsync<AcademicSourceMalformedException>(() => gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None));
        Assert.Equal("unexpected_content_type", exception.Details?.FailureCategory);
    }

    [Fact]
    public async Task Bad_json_is_categorized_as_malformed()
    {
        var (gateway, _) = Create((_, _) => AcademicFixture.Json("{truncated"));
        var exception = await Assert.ThrowsAsync<AcademicSourceMalformedException>(() => gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None));
        Assert.Equal("malformed", exception.Details?.FailureCategory);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation_not_timeout()
    {
        var (gateway, _) = Create((_, _) => AcademicFixture.Json("{}"));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.GetJsonAsync(OkEndpoint(), source.Token));
    }

    [Fact]
    public async Task Request_deadline_produces_a_timeout_category()
    {
        var hanging = new HangingHandler();
        var options = AcademicFixture.Defaults(options => options.RequestTimeoutSeconds = 2);
        var gateway = new AcademicHttpGateway(new HttpClient(hanging) { Timeout = Timeout.InfiniteTimeSpan }, options);
        await Assert.ThrowsAsync<AcademicSourceTimeoutException>(() => gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None));
    }

    [Fact]
    public async Task Pass_through_status_codes_reach_the_caller_without_payload_parsing()
    {
        var (gateway, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not json at all", System.Text.Encoding.UTF8, "application/json"),
        });
        var response = await gateway.GetJsonAsync(OkEndpoint(), CancellationToken.None, new HashSet<int> { 404 });
        Assert.Equal(404, response.StatusCode);
        Assert.False(response.IsSuccess);
        Assert.True(response.IsPassThroughStatus);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Sleep far past the gateway's 2-second request deadline; only the gateway's own
            // linked cancellation budget ends this call.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
