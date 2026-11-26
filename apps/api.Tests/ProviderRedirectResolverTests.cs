using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

// Review C on head 2a0d90ed: with the DI shadow fixed, the Manus/Runway/DirectVideo output
// fetches run on the pinned handler (AllowAutoRedirect=false), so a legitimate public
// 301/302/303/307/308 CDN hop — previously auto-followed by the shadowed default client — now
// fails outright because none of those adapters has Mubert's manual redirect loop. These tests
// pin the resolution: ProviderRedirectResolver follows bounded hops, but ONLY bodyless GET/HEAD
// artifact fetches, ONLY after IProviderUrlPolicy re-classifies each Location (https + 443 +
// public), never replays credentials to another origin, never follows or rewrites a mutating
// submission (3xx on POST is surfaced, not reissued), and surfaces refusals as
// provider-mappable failures.
public sealed class ProviderRedirectResolverTests
{
    private static readonly IPAddress PublicV4 = IPAddress.Parse("93.184.216.34");

    [Fact]
    public async Task Follows_public_absolute_hop_and_stops_at_success()
    {
        var handler = new StubPrimaryHandler(request => request.RequestUri!.Host == "out.example.test"
            ? Redirect(HttpStatusCode.Found, "https://cdn.example.test/v.mp4")
            : Ok());
        var (client, _) = Client(handler);

        using var response = await client.GetAsync("https://out.example.test/v.mp4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new[] { "https://out.example.test/v.mp4", "https://cdn.example.test/v.mp4" },
            handler.Requests.Select(request => request.Url));
    }

    [Fact]
    public async Task Resolves_relative_location_against_hop_uri()
    {
        var handler = new StubPrimaryHandler(request => request.RequestUri!.AbsolutePath == "/a/b"
            ? Redirect(HttpStatusCode.Moved, "/final")
            : Ok());
        var (client, _) = Client(handler);

        using var response = await client.GetAsync("https://out.example.test/a/b");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://out.example.test/final", handler.Requests[^1].Url);
    }

    [Fact]
    public async Task Preserves_head_and_get_methods_across_bounded_hops()
    {
        var handler = new StubPrimaryHandler(request => request.RequestUri!.Host == "out.example.test"
            ? Redirect(HttpStatusCode.SeeOther, "https://cdn.example.test/v.mp4")
            : Ok());
        var (client, _) = Client(handler);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://out.example.test/v.mp4"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // HEAD stays HEAD and 303 never rewrites GET/HEAD, matching the platform handler.
        Assert.All(handler.Requests, request => Assert.Equal("HEAD", request.Method));
    }

    // Reviews on bc10c885: the resolver wraps the whole typed client, including the PAID
    // generation POST submissions, whose create paths deliberately never retry. A provider
    // 3xx must therefore never reissue a mutating request — not as a 307/308 body replay
    // (cross-origin: another public origin could run a fresh billable generation with the
    // user's prompt; header stripping does not protect the body), and not as a 301/302/303
    // POST->GET rewrite of a provider-supplied target either. The 3xx is surfaced to the
    // provider's own failure mapping, exactly like the pre-resolver pinned handler.
    [Theory]
    [InlineData("301", true)]
    [InlineData("302", true)]
    [InlineData("303", true)]
    [InlineData("307", true)]
    [InlineData("308", true)]
    [InlineData("307", false)]
    [InlineData("308", false)]
    public async Task Mutating_submissions_never_follow_replay_or_rewrite_on_any_redirect(string code, bool crossOrigin)
    {
        var status = (HttpStatusCode)int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
        var target = crossOrigin ? "https://cdn.example.test/task" : "https://out.example.test/task-next";
        var handler = new StubPrimaryHandler(_ => Redirect(status, target));
        var (client, _) = Client(handler);

        // Realistic provider submission: private prompt JSON + provider credentials.
        using var submission = new HttpRequestMessage(HttpMethod.Post, "https://out.example.test/task")
        {
            Content = new StringContent(
                "{\"prompt\":\"user-secret-prompt\",\"referenceUrl\":\"https://files.example.test/private.png\"}",
                Encoding.UTF8, "application/json"),
        };
        submission.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "api-secret");
        submission.Headers.TryAddWithoutValidation("x-manus-api-key", "manus-secret");

        using var response = await client.SendAsync(submission);

        // Surfaced unchanged, zero follow-ups: no second request, so no second body, no
        // billable resend, and nothing private leaves toward the redirect target.
        Assert.Equal(status, response.StatusCode);
        var echo = Assert.Single(handler.Requests);
        Assert.Equal("POST", echo.Method);
        Assert.Equal("https://out.example.test/task", echo.Url);
        Assert.Contains("user-secret-prompt", echo.Body);
    }

    [Fact]
    public async Task Cross_origin_307_on_post_does_not_reach_the_redirect_target_at_all()
    {
        // The exact reviewed scenario: public cross-origin 307 from a submission POST. The
        // resolver must not even dial (let alone POST to) the lookalike target.
        var handler = new StubPrimaryHandler(_ =>
            Redirect(HttpStatusCode.TemporaryRedirect, "https://evil-public-cdn.example.test/generate"));
        var (client, _) = Client(handler);

        using var response = await client.PostAsync(
            "https://api.example.test/v1/generate",
            new StringContent("{\"prompt\":\"private\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(new[] { "https://api.example.test/v1/generate" }, handler.Requests.Select(request => request.Url));
    }

    [Theory]
    [InlineData("https://127.0.0.1/v.mp4", "Provider download target is not public.")]
    [InlineData("https://169.254.169.254/latest/meta-data", "Provider download target is not public.")]
    [InlineData("http://cdn.example.test/v.mp4", "Provider download URL is not safe.")]
    [InlineData("https://cdn.example.test:8443/v.mp4", "Provider download URL is not safe.")]
    [InlineData("https://user:pass@cdn.example.test/v.mp4", "Provider download URL is not safe.")]
    public async Task Refuses_hop_the_policy_rejects_before_dialing_it(string location, string expectedMessage)
    {
        var handler = new StubPrimaryHandler(_ => Redirect(HttpStatusCode.Found, location));
        var (client, _) = Client(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://out.example.test/v.mp4"));
        var inner = Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(expectedMessage, inner.Message);
        // The refused target is never dialed: the stub (the "socket") saw only the first hop.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Stops_after_bounded_hops_and_surfaces_final_redirect()
    {
        var hop = 0;
        var handler = new StubPrimaryHandler(_ => Redirect(
            HttpStatusCode.Found, $"https://cdn{hop++}.example.test/v.mp4"));
        var (client, _) = Client(handler);

        using var response = await client.GetAsync("https://out.example.test/v.mp4");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // initial request + exactly MaxRedirects followed hops, then the 3xx is surfaced.
        Assert.Equal(ProviderDownloadSecurity.MaxRedirects + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task Drops_credential_like_headers_only_when_the_hop_changes_origin()
    {
        var handler = new StubPrimaryHandler(request => request.RequestUri!.Host == "out.example.test"
            ? Redirect(HttpStatusCode.Found, "https://cdn.example.test/v.mp4")
            : Ok());
        var (client, _) = Client(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://out.example.test/v.mp4");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "api-secret");
        request.Headers.TryAddWithoutValidation("x-manus-api-key", "manus-secret");
        request.Headers.TryAddWithoutValidation("x-trace-token", "trace-1");
        request.Headers.TryAddWithoutValidation("x-preferred-codec", "h264");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var crossOrigin = handler.Requests[1].Headers;
        Assert.False(crossOrigin.ContainsKey("Authorization"));
        Assert.False(crossOrigin.ContainsKey("x-manus-api-key"));
        Assert.False(crossOrigin.ContainsKey("x-trace-token"));
        Assert.True(crossOrigin.ContainsKey("x-preferred-codec"));
    }

    [Fact]
    public async Task Keeps_headers_on_same_origin_hop()
    {
        var handler = new StubPrimaryHandler(request => request.RequestUri!.AbsolutePath == "/v.mp4"
            ? Redirect(HttpStatusCode.Found, "https://out.example.test/final.mp4")
            : Ok());
        var (client, _) = Client(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://out.example.test/v.mp4");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "api-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(handler.Requests[1].Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Non_redirect_responses_pass_straight_through()
    {
        var handler = new StubPrimaryHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var (client, _) = Client(handler);

        using var response = await client.GetAsync("https://out.example.test/v.mp4");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    private static (HttpClient Client, ProviderRedirectResolver Resolver) Client(StubPrimaryHandler handler)
    {
        // Real policy over a table resolver that answers every hostname with one public
        // address, exactly what a live provider CDN lookup returns.
        var resolver = new ProviderRedirectResolver(new ProviderUrlPolicy(new PublicAddressResolver()))
        {
            InnerHandler = handler,
        };
        return (new HttpClient(resolver), resolver);
    }

    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(string.Empty) };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("clip", Encoding.UTF8, "video/mp4") };

    private sealed class PublicAddressResolver : IProviderHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(new[] { PublicV4 });
    }

    private sealed class StubPrimaryHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestEcho> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(RequestEcho.Capture(request));
            return Task.FromResult(respond(request));
        }
    }

    // Snapshotted at send time because the resolver disposes per-hop requests as it advances.
    private sealed record RequestEcho(
        string Method,
        string Url,
        Dictionary<string, string[]> Headers,
        string? Body)
    {
        public static RequestEcho Capture(HttpRequestMessage request)
        {
            var headers = request.Headers
                .ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            string? body = null;
            if (request.Content is not null)
            {
                var bytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                body = bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
            }

            return new RequestEcho(request.Method.Method, request.RequestUri!.ToString(), headers, body);
        }
    }
}

// Behavioral tests through the REAL ManusMovieVideoProvider (the review asked for provider
// paths, not handler toys): the shipped RetrieveAsync metadata flow plus the shipped resolver
// plus the shipped policy. Public 3xx output hops succeed again; private redirect targets stay
// rejected and are never dialed.
public sealed class ManusOutputRedirectCompatibilityTests
{
    private static readonly IPAddress PublicV4 = IPAddress.Parse("93.184.216.34");

    [Fact]
    public async Task RetrieveAsync_follows_public_output_redirect_end_to_end()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host == "out.example.test"
            ? Redirect(HttpStatusCode.Found, "https://cdn.example.test/v.mp4")
            : VideoBody());
        var (provider, _) = Provider(handler);

        var output = await provider.RetrieveAsync("task-1", SucceededStatus("https://out.example.test/v.mp4"), CancellationToken.None);
        await using var stream = await output.OpenReadAsync(CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal("clip", Encoding.UTF8.GetString(buffer.ToArray()));
        Assert.Equal(2, handler.Urls.Count);
        Assert.Equal("https://cdn.example.test/v.mp4", handler.Urls[^1]);
    }

    [Fact]
    public async Task RetrieveAsync_refuses_private_output_redirect_target()
    {
        var handler = new RecordingHandler(_ => Redirect(HttpStatusCode.Found, "https://127.0.0.1/v.mp4"));
        var (provider, _) = Provider(handler);

        var output = await provider.RetrieveAsync("task-1", SucceededStatus("https://out.example.test/v.mp4"), CancellationToken.None);
        var error = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => output.OpenReadAsync(CancellationToken.None));
        Assert.NotNull(error);
        // Refusal happens before any socket: only the admitted first hop was ever requested.
        Assert.Equal(new[] { "https://out.example.test/v.mp4" }, handler.Urls);
    }

    // Full provider flow over the #1273-aligned classifier: a DNS64-spelled public output
    // literal (the answer an IPv6-only deployment synthesises for a public provider CDN) must
    // fetch, while the same prefix spelling embedded private/metadata space must be refused
    // before anything is dialed.
    [Fact]
    public async Task RetrieveAsync_fetches_dns64_public_output_literal_end_to_end()
    {
        var handler = new RecordingHandler(_ => VideoBody());
        var (provider, _) = Provider(handler);

        var output = await provider.RetrieveAsync("task-1",
            SucceededStatus("https://[64:ff9b::808:808]/v.mp4"), CancellationToken.None);
        await using var stream = await output.OpenReadAsync(CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        Assert.Equal("clip", Encoding.UTF8.GetString(buffer.ToArray()));
        Assert.Equal(new[] { new Uri("https://[64:ff9b::808:808]/v.mp4").ToString() }, handler.Urls);
    }

    [Fact]
    public async Task RetrieveAsync_refuses_nat64_metadata_spelled_output_literal()
    {
        var handler = new RecordingHandler(_ => VideoBody());
        var (provider, _) = Provider(handler);

        await Assert.ThrowsAsync<MovieVideoProviderOutputException>(() => provider.RetrieveAsync(
            "task-1", SucceededStatus("https://[64:ff9b::a9fe:a9fe]/v.mp4"), CancellationToken.None));
        Assert.Empty(handler.Urls);
    }

    private static (ManusMovieVideoProvider Provider, List<string> Unused) Provider(RecordingHandler handler)
    {
        var policy = new ProviderUrlPolicy(new PublicAddressResolver());
        var resolver = new ProviderRedirectResolver(policy) { InnerHandler = handler };
        var httpClient = new HttpClient(resolver);
        var options = Options.Create(new MovieVideoOptions
        {
            Enabled = true,
            ProviderKey = "manus",
            Manus = new ManusMovieVideoOptions { Enabled = true, ApiKey = "test-key" },
        });
        var provider = new ManusMovieVideoProvider(httpClient, options, NullLogger<ManusMovieVideoProvider>.Instance, policy);
        return (provider, new List<string>());
    }

    private static MovieVideoProviderStatus SucceededStatus(string outputUrl) =>
        new(MovieVideoProviderJobStatus.Succeeded, 100, MetadataJson:
            $"{{\"providerTaskId\":\"task-1\",\"outputUrl\":\"{outputUrl}\",\"contentType\":\"video/mp4\",\"fileName\":\"v.mp4\",\"sizeBytes\":4}}");

    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(string.Empty) };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage VideoBody() =>
        new(HttpStatusCode.OK) { Content = new StringContent("clip", Encoding.UTF8, "video/mp4") };

    private sealed class PublicAddressResolver : IProviderHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(new[] { PublicV4 });
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }
}

// Availability assessment for the same review round: the platform connect path previously tried
// every resolved address before giving up, so pinning a single answer entry could have been a
// regression for multi-A-record provider CDNs. These tests pin the restored fallback: candidates
// come from ONE validated resolution answer, in order (IPv4 first), and a connect failure walks
// the rest of the answer without ever re-resolving (no DNS-rebinding window reopens) or dialing
// an unvalidated address.
public sealed class ProviderPinnedConnectionFallbackTests
{
    private static readonly IPAddress PublicV4 = IPAddress.Parse("93.184.216.34");
    private static readonly IPAddress PublicV6 = IPAddress.Parse("2606:4700:4700::1111");

    [Fact]
    public async Task Falls_back_to_next_validated_address_with_single_resolution()
    {
        var resolver = new CountingResolver(new[] { PublicV6, PublicV4 });
        var dialed = new List<(IPAddress Address, int Port)>();
        var attempts = 0;
        ValueTask<Stream> Transport(IPAddress address, int port, CancellationToken token)
        {
            dialed.Add((address, port));
            attempts++;
            if (attempts == 1)
                return ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused));
            return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("connected")));
        }

        await using var stream = await ProviderPinnedConnections.OpenConnectionAsync(
            "api.example.test", 443, resolver, Transport, CancellationToken.None);

        Assert.Equal(new[] { (PublicV4, 443), (PublicV6, 443) }, dialed);
        Assert.Equal(1, resolver.CallCount);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("connected", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Surfaces_last_failure_when_every_validated_address_is_down()
    {
        var resolver = new CountingResolver(new[] { PublicV4, PublicV6 });
        var dialed = new List<IPAddress>();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            ProviderPinnedConnections.OpenConnectionAsync(
                "api.example.test", 443, resolver,
                (address, port, token) =>
                {
                    dialed.Add(address);
                    return ValueTask.FromException<Stream>(new IOException($"down-{address}"));
                },
                CancellationToken.None));

        Assert.Equal(new[] { PublicV4, PublicV6 }, dialed);
        Assert.Contains("down-2606", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Never_re_dials_unvalidated_or_private_candidates()
    {
        // Admission is all-or-nothing BEFORE any dial: a mixed answer must not even reach the
        // fallback loop, so a down-but-public first address cannot be "stepped over" into a
        // private sibling.
        var resolver = new CountingResolver(new[] { PublicV4, IPAddress.Loopback });
        var dialed = false;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProviderPinnedConnections.OpenConnectionAsync(
                "api.example.test", 443, resolver,
                (address, port, token) =>
                {
                    dialed = true;
                    return ValueTask.FromResult<Stream>(new MemoryStream());
                },
                CancellationToken.None));

        Assert.Equal("Provider download target is not public.", error.Message);
        Assert.False(dialed);
    }

    [Fact]
    public async Task Ip_literal_failure_propagates_without_resolver_calls()
    {
        var resolver = new CountingResolver(Array.Empty<IPAddress>());

        var error = await Assert.ThrowsAsync<SocketException>(() =>
            ProviderPinnedConnections.OpenConnectionAsync(
                "93.184.216.34", 443, resolver,
                (address, port, token) => ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)),
                CancellationToken.None));

        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
        Assert.Equal(0, resolver.CallCount);
    }

    private sealed class CountingResolver(IReadOnlyList<IPAddress> answer) : IProviderHostResolver
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(answer);
        }
    }
}
