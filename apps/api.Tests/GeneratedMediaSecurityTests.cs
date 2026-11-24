using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GeneratedMediaSecurityTests
{
    [Fact]
    public void Filename_is_sanitized_and_extension_is_canonicalized()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("../../safe-name.mp3", "audio/mpeg");

        Assert.Equal("safe-name.mp3", descriptor.SafeFileName);
        Assert.Equal(".mp3", descriptor.Extension);
        Assert.Equal("audio/mpeg", descriptor.ContentType);
    }

    [Fact]
    public void MIME_mismatch_is_rejected_before_storage_acceptance()
    {
        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateDescriptor("image.png", "image/jpeg"));
    }

    [Fact]
    public void HTML_cannot_masquerade_as_an_image()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("image.png", "image/png");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, Encoding.UTF8.GetBytes("<html>error</html>"), new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public async Task Oversized_stream_is_stopped_by_the_bounded_reader()
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
        using var bounded = new CountingReadStream(source, 4);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.CopyToAsync(destination));
        Assert.True(bounded.BytesRead <= 4);
    }

    [Fact]
    public async Task Exact_size_stream_is_hashed_without_buffering_the_payload()
    {
        var content = Encoding.UTF8.GetBytes("streamed generated media");
        using var source = new MemoryStream(content);
        using var bounded = new CountingReadStream(source, content.Length);
        using var destination = new MemoryStream();

        await bounded.CopyToAsync(destination);

        Assert.Equal(content, destination.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), bounded.Sha256Hex);
    }

    [Fact]
    public async Task Exact_length_verification_finalizes_hash_when_s3_stops_at_content_length()
    {
        var bytes = Encoding.UTF8.GetBytes("generated video payload");
        using var source = new MemoryStream(bytes);
        using var bounded = new CountingReadStream(source, maxBytes: 128, expectedContentLength: bytes.Length);
        var requestBytes = new byte[bytes.Length];

        // Simulate an S3 adapter that reads exactly Content-Length, never EOF.
        Assert.Equal(bytes.Length, await bounded.ReadAsync(requestBytes.AsMemory()));
        Assert.Equal(bytes, requestBytes);
        Assert.Null(bounded.Sha256Hex);

        await bounded.VerifyExactLengthAsync(bytes.Length);

        Assert.Equal(bytes.Length, bounded.BytesRead);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), bounded.Sha256Hex);
    }

    [Fact]
    public async Task Exact_length_verification_rejects_trailing_bytes_hidden_by_content_length()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var bounded = new CountingReadStream(source, maxBytes: 10, expectedContentLength: 3);
        var requestBytes = new byte[3];

        Assert.Equal(3, await bounded.ReadAsync(requestBytes.AsMemory()));
        await Assert.ThrowsAsync<FileUploadValidationException>(
            () => bounded.VerifyExactLengthAsync(3));
    }

    [Fact]
    public async Task Exact_length_verification_rejects_short_generated_stream()
    {
        using var source = new MemoryStream([1, 2]);
        using var bounded = new CountingReadStream(source, maxBytes: 10, expectedContentLength: 3);
        using var destination = new MemoryStream();

        await bounded.CopyToAsync(destination);
        await Assert.ThrowsAsync<FileUploadValidationException>(
            () => bounded.VerifyExactLengthAsync(3));
    }

    [Fact]
    public void Image_inspection_derives_container_and_dimensions_from_the_header()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("frame.png", "image/png");
        var content = new byte[32];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(content, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(content.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(content.AsSpan(16, 4), 640);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(content.AsSpan(20, 4), 360);

        var inspection = GeneratedMediaInspector.Inspect(descriptor, content, null, "ABCD");

        Assert.Equal("png", inspection.ContainerFormat);
        Assert.Equal(640, inspection.Width);
        Assert.Equal(360, inspection.Height);
        Assert.Equal("ABCD", inspection.ContentHashSha256);
    }

    [Fact]
    public void OpenXml_path_traversal_is_rejected()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", "<Types/>");
            AddEntry(archive, "word/document.xml", "<document/>");
            AddEntry(archive, "../../escape.txt", "do not extract");
        }

        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(
            "report.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, output.ToArray(), new Taslim.Api.Files.FileOptions()));
    }

    [Theory]
    [InlineData("https://127.0.0.1/media.mp3")]
    [InlineData("https://10.0.0.8/media.mp3")]
    [InlineData("https://100.64.0.1/media.mp3")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://192.0.0.1/media.mp3")]
    [InlineData("https://192.0.2.1/media.mp3")]
    [InlineData("https://198.18.0.1/media.mp3")]
    [InlineData("https://198.51.100.1/media.mp3")]
    [InlineData("https://203.0.113.1/media.mp3")]
    [InlineData("https://[::1]/media.mp3")]
    [InlineData("https://[2001:db8::1]/media.mp3")]
    public async Task Provider_url_policy_blocks_private_metadata_and_reserved_targets(string value)
    {
        var policy = new ProviderUrlPolicy();

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri(value)));
    }

    // Special-use ranges the provider guard must refuse that a naive loopback/private/multicast
    // sweep misses. 192.88.99.0/24 is the deprecated 6to4 relay anycast address; 2002::/16 embeds
    // an attacker-chosen IPv4 in bytes 2-5; 2001::/32 is Teredo; 100::/64 is discard-only and
    // 3ffe::/16 is legacy 6bone. Kept aligned with AcademicSourceUrlGuard.IsPubliclyRoutable so
    // the repository has one SSRF address standard rather than two.
    [Theory]
    [InlineData("https://192.88.99.1/media.mp3")]
    [InlineData("https://[2002:7f00:1::]/media.mp3")]
    [InlineData("https://[2001:0000:0000::1]/media.mp3")]
    [InlineData("https://[100::1]/media.mp3")]
    [InlineData("https://[3ffe::1]/media.mp3")]
    public async Task Provider_url_policy_blocks_special_use_ipv6_and_anycast_targets(string value)
    {
        var policy = new ProviderUrlPolicy();

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri(value)));
    }

    // An IPv4 destination written inside an IPv6 literal must be judged by the IPv4 rules, not by
    // the IPv6 ones. ::ffff:/96 was already reduced; ::/96 (deprecated IPv4-compatible) was not,
    // so [::a9fe:a9fe] used to read as a routable v6 endpoint while naming 169.254.169.254.
    [Theory]
    [InlineData("https://[::ffff:169.254.169.254]/media.mp3")]
    [InlineData("https://[::ffff:127.0.0.1]/media.mp3")]
    [InlineData("https://[::a9fe:a9fe]/media.mp3")]
    [InlineData("https://[::7f00:1]/media.mp3")]
    [InlineData("https://[::a00:1]/media.mp3")]
    [InlineData("https://[::]/media.mp3")]
    public async Task Provider_url_policy_reduces_embedded_ipv6_literals_to_ipv4_rules(string value)
    {
        var policy = new ProviderUrlPolicy();

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri(value)));
    }

    // Positive control: the guard must stay fail-closed without becoming fail-everything. Real
    // provider CDN endpoints are plain public addresses, and the widened v6 rules must not swallow
    // 2001:4860::/32 (Google DNS) or 2606:4700::/32 (Cloudflare) just because they start 20 01.
    [Theory]
    [InlineData("https://1.1.1.1/media.mp3")]
    [InlineData("https://8.8.8.8/media.mp3")]
    [InlineData("https://[2606:4700:4700::1111]/media.mp3")]
    [InlineData("https://[2001:4860:8000::68]/media.mp3")]
    [InlineData("https://[::10a0:1]/media.mp3")]
    public async Task Provider_url_policy_still_allows_public_targets(string value)
    {
        var policy = new ProviderUrlPolicy();

        await policy.EnsureSafeAsync(new Uri(value));
    }

    [Fact]
    public async Task Provider_url_policy_requires_https()
    {
        var policy = new ProviderUrlPolicy();

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri("http://example.com/media.mp3")));
    }

    [Fact]
    public async Task Provider_url_policy_rejects_non_default_https_ports()
    {
        var policy = new ProviderUrlPolicy();

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri("https://example.com:8443/media.mp3")));
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8, 1024, leaveOpen: false);
        writer.Write(content);
    }
}

// Composition proof for the provider-download redirect lane (PR #358). The typed clients that
// fetch provider-controlled URLs must never let the framework follow a 3xx hop on its own: a
// redirect destination arrives from provider response data and is only safe once
// IProviderUrlPolicy has classified it. The review on e9903fa8 proved two gaps this class now
// closes: (1) the option-level assertions passed while bare AddSingleton registrations shadowed
// the typed clients, so the registry resolved providers whose effective handler still followed
// redirects, and (2) a plain HttpClientHandler re-resolved the hostname at connect time, opening
// a DNS-rebinding window after the policy check. The four policy-guarded clients therefore ship
// a pinned SocketsHttpHandler (no auto-redirect, no proxy, connect callback dialed to exactly
// the validated address); StableAudioMusicGenerationProvider never consumes the policy — its
// fetch target is operator configuration, not provider response data — so it keeps the plain
// HttpClientHandler redirect flag as the positive control for that convention.
public sealed class ProviderDownloadRedirectHardeningTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ProviderDownloadRedirectHardeningTests(TaslimApiFactory factory) => this.factory = factory;

    public static TheoryData<Type, bool, bool> GuardedClientTypes { get; } = new()
    {
        // (addressPinned, followsClassifiedHops): the video adapters' fetch code has no redirect
        // loop, so the bounded revalidating resolver must be wired for them. Mubert keeps its own
        // manual loop and must NOT gain a second one; StableAudio's audio arrives inline in its
        // 200 API responses (no provider-URL download hop exists), so nothing must be wired.
        { typeof(ManusMovieVideoProvider), true, true },
        { typeof(RunwayMovieVideoProvider), true, true },
        { typeof(DirectVideoHttpProviderAdapter), true, true },
        { typeof(Taslim.Api.Music.MubertMusicGenerationProvider), true, false },
        { typeof(Taslim.Api.Music.StableAudioMusicGenerationProvider), false, false },
    };

    [Theory]
    [MemberData(nameof(GuardedClientTypes))]
    public void Guarded_provider_client_never_autofollows_unclassified_redirect_hops(Type clientType, bool addressPinned, bool followsClassifiedHops)
    {
        // AddHttpClient<TClient>() keys its named options by the type's simple name on net8.
        var options = factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Http.HttpClientFactoryOptions>>()
            .Get(clientType.Name);

        var probe = new PrimaryHandlerProbe(clientType.Name, factory.Services);
        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(probe);
        }

        var primary = probe.PrimaryHandler;
        Assert.NotNull(primary);
        try
        {
            if (addressPinned)
            {
                // Policy-guarded clients must be the pinned composition: redirect flag off, no
                // proxy (a proxy resolves and dials wherever it likes, bypassing the pin), and a
                // connect callback that binds each connection to the validated address.
                var handler = Assert.IsType<SocketsHttpHandler>(primary);
                Assert.False(handler.AllowAutoRedirect,
                    $"{clientType.Name}: AllowAutoRedirect must be false; a redirect destination is provider response data and is only safe once IProviderUrlPolicy has classified it");
                Assert.False(handler.UseProxy,
                    $"{clientType.Name}: UseProxy must be false; a proxying connect would dial an address the policy never admitted");
                Assert.NotNull(handler.ConnectCallback);
            }
            else
            {
                // The lane keeps a plain redirect-blocked HttpClientHandler for clients without
                // a policy dependency; anything else is a convention change to review as such.
                var handler = Assert.IsType<HttpClientHandler>(primary);
                Assert.False(handler.AllowAutoRedirect,
                    $"{clientType.Name}: AllowAutoRedirect must be false; a redirect destination is provider response data and is only safe once IProviderUrlPolicy has classified it");
            }
        }
        finally
        {
            primary!.Dispose();
        }

        // Framework auto-redirect must stay off, while adapters that rely on legitimate public
        // 3xx output hops get the policy-revalidated resolver in the SAME effective pipeline.
        var resolvers = probe.AdditionalHandlers.OfType<ProviderRedirectResolver>().ToList();
        if (followsClassifiedHops) Assert.Single(resolvers);
        else Assert.Empty(resolvers);
        foreach (var resolver in resolvers) resolver.Dispose();
    }

    // The e9903fa8 assertions above read registration, not resolution. Review B proved that
    // `AddHttpClient<ManusMovieVideoProvider>()` followed by `AddSingleton<ManusMovieVideoProvider>()`
    // resolves the SINGLETON (Microsoft DI last-wins) — built on the container's default HttpClient,
    // innermost HttpClientHandler with AllowAutoRedirect=true — so the configured primary handler
    // never reached the providers the registry uses. This test resolves the real registry and
    // inspects the effective client of every provider it holds: on the old composition it fails
    // with innermost=HttpClientHandler for Manus and Runway.
    [Fact]
    public void Movie_video_registry_resolves_pinned_redirect_blocked_providers()
    {
        var registry = factory.Services.GetRequiredService<IMovieVideoProviderRegistry>();
        using var scope = factory.Services.CreateScope();
        Assert.Same(registry, scope.ServiceProvider.GetRequiredService<IMovieVideoProviderRegistry>());

        var providersField = typeof(MovieVideoProviderRegistry)
            .GetField("providers", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(providersField);
        var providers = (IReadOnlyDictionary<string, IMovieVideoProvider>)providersField!.GetValue(registry)!;

        Assert.Contains(providers.Values, provider => provider.GetType() == typeof(RunwayMovieVideoProvider));
        Assert.Contains(providers.Values, provider => provider.GetType() == typeof(ManusMovieVideoProvider));

        foreach (var provider in providers.Values)
        {
            var client = ProviderClientInspection.EffectiveClient(provider);
            var handler = Assert.IsType<SocketsHttpHandler>(ProviderClientInspection.InnermostHandler(client));
            Assert.False(handler.AllowAutoRedirect,
                $"{provider.GetType().Name}: the client the registry actually holds must not follow redirects");
            Assert.False(handler.UseProxy);
            Assert.NotNull(handler.ConnectCallback);

            // Review C's compatibility half: the effective client resolved THROUGH THE REAL
            // REGISTRY must also contain the bounded revalidating redirect resolver, or a
            // legitimate public 3xx CDN hop would now fail the fetch.
            var pipelineField = typeof(HttpMessageInvoker)
                .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pipeline = pipelineField.GetValue(client) as HttpMessageHandler;
            var hasResolver = false;
            while (pipeline is DelegatingHandler delegating)
            {
                if (delegating is ProviderRedirectResolver) hasResolver = true;
                pipeline = delegating.InnerHandler;
            }

            Assert.True(hasResolver,
                $"{provider.GetType().Name}: the registry client must follow policy-revalidated redirects");
        }
    }

    // Review B asked for a proof through the real resolution with a redirecting test handler.
    // The provider client resolved out of the registry cannot even open a socket to the
    // redirecting loopback listener: the connect callback classifies 127.0.0.1 and refuses, so
    // neither an automatic hop nor a provider tricked into a loopback Location: can be fetched.
    [Fact]
    public async Task Registry_movie_client_refuses_to_dial_the_loopback_redirect_target()
    {
        var (listener, baseUrl, paths) = await StartRedirectListenerAsync();
        try
        {
            var registry = factory.Services.GetRequiredService<IMovieVideoProviderRegistry>();
            var providersField = typeof(MovieVideoProviderRegistry)
                .GetField("providers", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var providers = (IReadOnlyDictionary<string, IMovieVideoProvider>)providersField.GetValue(registry)!;
            var provider = providers.Values.Single(value => value.GetType() == typeof(ManusMovieVideoProvider));

            var client = ProviderClientInspection.EffectiveClient(provider);
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(baseUrl + "start"));
            Assert.Equal("Provider download target is not public.", error.GetBaseException().Message);
            Assert.Empty(ProviderClientInspection.Snapshot(paths));
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    // Public-API stand-in for the factory's internal DefaultHttpMessageHandlerBuilder: the
    // named options' builder actions capture the ConfigurePrimaryHttpMessageHandler delegate
    // in PrimaryHandler, which is exactly the registration shipped by Program.cs.
    private sealed class PrimaryHandlerProbe : Microsoft.Extensions.Http.HttpMessageHandlerBuilder
    {
        public PrimaryHandlerProbe(string name, IServiceProvider services)
        {
            Name = name;
            ServicesValue = services;
        }

        public override string? Name { get; set; }

        public override System.Net.Http.HttpMessageHandler? PrimaryHandler { get; set; }

        public override IList<System.Net.Http.DelegatingHandler> AdditionalHandlers { get; } =
            new List<System.Net.Http.DelegatingHandler>();

        private IServiceProvider ServicesValue { get; }

        public override IServiceProvider Services => ServicesValue;

        public override System.Net.Http.HttpMessageHandler Build() =>
            PrimaryHandler ?? throw new InvalidOperationException("probe builders never call Build");
    }

    private static async Task<(HttpListener Listener, string BaseUrl, List<string> Paths)> StartRedirectListenerAsync()
    {
        var port = FreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var paths = new List<string>();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                lock (paths)
                {
                    paths.Add(path);
                }

                if (string.Equals(path, "/start", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Found;
                    context.Response.RedirectLocation = "/final";
                }
                else
                {
                    var body = Encoding.UTF8.GetBytes("clip");
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "video/mp4";
                    await context.Response.OutputStream.WriteAsync(body);
                }

                context.Response.Close();
            }
        });
        return (listener, $"http://127.0.0.1:{port}/", paths);
    }

    private static int FreeTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

// DNS-rebinding/TOCTOU proof for the provider-download lane (PR #358 review). The old guard
// resolved a hostname in EnsureSafeAsync and then handed the connection to a plain
// HttpClientHandler, which resolved it a SECOND time at connect — DNS could flip between the
// two answers. Every test here drives one injectable resolver through both the policy decision
// and the dial decision, so the reproduced failure mode (public answer first, private answer
// second, socket lands on private) and the fixed behaviour are asserted against the shipped
// code paths, not a simulation.
public sealed class ProviderPinnedConnectionTests
{
    private static readonly IPAddress PublicV4 = IPAddress.Parse("93.184.216.34");
    private static readonly IPAddress PublicV4B = IPAddress.Parse("8.8.8.8");
    private static readonly IPAddress PublicV6 = IPAddress.Parse("2606:4700:4700::1111");
    private static readonly IPAddress LoopbackV4 = IPAddress.Loopback;
    private static readonly IPAddress Metadata = IPAddress.Parse("169.254.169.254");

    [Fact]
    public async Task Rebinding_answer_that_flips_public_to_private_after_the_policy_pass_is_refused()
    {
        // Exactly the reviewed window: call 1 (policy) answers public, call 2 (connection)
        // answers loopback. The dial re-enters admission with the same resolver and must refuse
        // before any transport sees an address.
        var resolver = new QueueResolver(call => call == 1 ? new[] { PublicV4 } : new[] { LoopbackV4 });
        var policy = new ProviderUrlPolicy(resolver);
        await policy.EnsureSafeAsync(new Uri("https://provider.example.test/clip.mp4"));

        var dialed = new List<(IPAddress Address, int Port)>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProviderPinnedConnections.OpenConnectionAsync("provider.example.test", 443, resolver, StubTransport(dialed), default));

        Assert.Equal("Provider download target is not public.", error.Message);
        Assert.Empty(dialed);
        Assert.Equal(2, resolver.CallCount);
    }

    [Theory]
    [InlineData("public-first")]
    [InlineData("private-first")]
    public async Task Mixed_public_private_answer_refuses_the_whole_target(string order)
    {
        // All-or-nothing admission: a resolver answer that CONTAINS a non-public address is a
        // refusal, never a silent selection of the public sibling. This is what makes an
        // alternating [public, loopback] rebinding answer useless to an attacker even when the
        // policy pass and the connect pass see the same A/record set.
        IReadOnlyList<IPAddress> answer = order == "public-first"
            ? new[] { PublicV4, LoopbackV4 }
            : new[] { Metadata, PublicV4 };
        var resolver = new QueueResolver(_ => answer);
        var policy = new ProviderUrlPolicy(resolver);

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri("https://provider.example.test/clip.mp4")));

        var dialed = new List<(IPAddress Address, int Port)>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProviderPinnedConnections.OpenConnectionAsync("provider.example.test", 443, resolver, StubTransport(dialed), default));
        Assert.Equal("Provider download target is not public.", error.Message);
        Assert.Empty(dialed);
    }

    [Fact]
    public async Task Checked_address_is_dialed_address_with_exactly_one_resolver_call_per_connection()
    {
        // One connection = one resolver call, and the transport receives precisely the validated
        // address (checked == dialed), never the hostname to resolve on its own. Dual-stack
        // answers pin to IPv4 so one operation stays on one family.
        var resolver = new QueueResolver(_ => new[] { PublicV6, PublicV4 });
        var dialed = new List<(IPAddress Address, int Port)>();

        await ProviderPinnedConnections.OpenConnectionAsync("Provider.Example.TEST.", 8443, resolver, StubTransport(dialed), default);

        Assert.Equal(1, resolver.CallCount);
        Assert.Equal("provider.example.test", resolver.Hosts[0]);
        Assert.Equal(new[] { (PublicV4, 8443) }, dialed);
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("[2606:4700:4700::1111]")]
    [InlineData("[::ffff:8.8.8.8]")]
    public async Task Ip_literals_are_classified_without_touching_the_resolver(string host)
    {
        var resolver = new QueueResolver(_ =>
        {
            throw new InvalidOperationException("IP literals must be classified directly");
        });

        var pinned = await ProviderPinnedConnections.SelectPinnedAddressAsync(host, resolver, default);

        Assert.True(IPAddress.TryParse(host.TrimStart('[').TrimEnd(']'), out var expected));
        Assert.NotNull(pinned);
        Assert.Equal(expected.ToString(), pinned!.ToString());
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("[::1]")]
    public async Task Non_public_ip_literals_are_refused_without_touching_the_resolver(string host)
    {
        var resolver = new QueueResolver(_ => new[] { PublicV4 });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProviderPinnedConnections.SelectPinnedAddressAsync(host, resolver, default));

        Assert.Equal("Provider download target is not public.", error.Message);
        Assert.Equal(0, resolver.CallCount);
    }

    // The reviewed production bypass: `*.example.test` returned from EnsureSafeAsync without any
    // resolution, so a private DNS serving 169.254.169.254 for a reserved name decided the fetch
    // target. The bypass is gone — reserved names now flow through the (injectable, test-only)
    // resolver like every other hostname, and these fakes are what tests use instead of it.
    [Fact]
    public async Task Reserved_test_names_are_resolved_and_classified_not_bypassed()
    {
        var publicResolver = new QueueResolver(_ => new[] { PublicV4 });
        var publicPolicy = new ProviderUrlPolicy(publicResolver);

        await publicPolicy.EnsureSafeAsync(new Uri("https://api.example.test/v1/task"));

        Assert.Equal(1, publicResolver.CallCount);

        var privateResolver = new QueueResolver(_ => new[] { LoopbackV4 });
        var privatePolicy = new ProviderUrlPolicy(privateResolver);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            privatePolicy.EnsureSafeAsync(new Uri("https://api.example.test/v1/task")));
        Assert.Equal("Provider download target is not public.", error.Message);
        Assert.Equal(1, privateResolver.CallCount);
    }

    [Fact]
    public async Task Empty_resolver_answer_is_a_rejection_not_an_approval()
    {
        var resolver = new QueueResolver(_ => Array.Empty<IPAddress>());
        var policy = new ProviderUrlPolicy(resolver);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri("https://provider.example.test/clip.mp4")));
        Assert.Equal("Provider download target is not public.", error.Message);
    }

    [Fact]
    public async Task Resolver_failure_keeps_the_stable_safety_message()
    {
        var policy = new ProviderUrlPolicy(new ThrowingResolver());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri("https://provider.example.test/clip.mp4")));

        Assert.Equal("Provider download host could not be resolved safely.", error.Message);
        Assert.IsType<SocketException>(error.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[::1")]
    [InlineData("fe80::1%eth0")]
    [InlineData("[]")]
    public async Task Unusable_host_forms_are_refused_before_any_dial(string host)
    {
        var resolver = new QueueResolver(_ => new[] { PublicV4 });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProviderPinnedConnections.SelectPinnedAddressAsync(host, resolver, default));

        Assert.Equal("Provider download URL is not safe.", error.Message);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public void Pinned_handler_ships_the_compatibility_flags_the_lane_promises()
    {
        using var handler = ProviderPinnedConnections.CreatePinnedHandler(new QueueResolver(_ => new[] { PublicV4 }));

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task Public_literal_targets_pass_the_policy_with_zero_resolver_calls()
    {
        // Positive control for provider compatibility: real provider CDN endpoints are plain
        // public addresses (the Mubert redirect regression uses 1.1.1.1) and must keep passing
        // through the same decision function the socket uses.
        var resolver = new QueueResolver(_ =>
        {
            throw new InvalidOperationException("IP literals must not reach the resolver");
        });
        var policy = new ProviderUrlPolicy(resolver);

        await policy.EnsureSafeAsync(new Uri("https://1.1.1.1/track.mp3"));

        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task Loopback_redirect_service_is_unreachable_even_by_manual_retry_of_the_shipped_config()
    {
        // Live-socket proof of the redirect semantics the lane ships:
        //   1. the DEFAULT HttpClientHandler follows 302 to the loopback target (the pre-fix
        //      behaviour — provider response data fetched with no classification);
        //   2. the shipped flag combination (SocketsHttpHandler, AllowAutoRedirect=false) returns
        //      the 302 to provider code and never requests the target itself;
        //   3. the pinned handler created by production composition refuses even to dial the
        //      loopback listener, because its connect callback runs the same admission as the
        //      policy — so a manual per-hop retry (the Mubert loop) also cannot land there.
        var (listener, baseUrl, paths) = await StartRedirectListenerAsync();
        try
        {
            using (var follow = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }))
            {
                var response = await follow.GetAsync(baseUrl + "start");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.Equal(new[] { "/start", "/final" }, ProviderClientInspection.Snapshot(paths));

            ProviderClientInspection.Clear(paths);

            using (var blocked = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
            }))
            {
                var response = await blocked.GetAsync(baseUrl + "start");
                Assert.Equal(HttpStatusCode.Found, response.StatusCode);
                Assert.Equal("/final", response.Headers.Location?.ToString());
            }

            Assert.Equal(new[] { "/start" }, ProviderClientInspection.Snapshot(paths));

            ProviderClientInspection.Clear(paths);

            using var pinned = ProviderPinnedConnections.CreatePinnedHandler(new QueueResolver(_ => new[] { PublicV4 }));
            using (var pinnedClient = new HttpClient(pinned))
            {
                var error = await Assert.ThrowsAsync<HttpRequestException>(() => pinnedClient.GetAsync(baseUrl + "start"));
                Assert.Equal("Provider download target is not public.", error.GetBaseException().Message);
            }

            Assert.Empty(ProviderClientInspection.Snapshot(paths));
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    private static Func<IPAddress, int, CancellationToken, ValueTask<Stream>> StubTransport(List<(IPAddress Address, int Port)> dialed) =>
        (address, port, _) =>
        {
            dialed.Add((address, port));
            return ValueTask.FromResult<Stream>(new MemoryStream());
        };

    private sealed class QueueResolver(Func<int, IReadOnlyList<IPAddress>> answer) : IProviderHostResolver
    {
        public List<string> Hosts { get; } = new();

        public int CallCount => Hosts.Count;

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Hosts.Add(host);
            return Task.FromResult(answer(Hosts.Count));
        }
    }

    private sealed class ThrowingResolver : IProviderHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<IPAddress>>(new SocketException((int)SocketError.HostNotFound));
    }

    private static async Task<(HttpListener Listener, string BaseUrl, List<string> Paths)> StartRedirectListenerAsync()
    {
        var port = FreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var paths = new List<string>();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                lock (paths)
                {
                    paths.Add(path);
                }

                if (string.Equals(path, "/start", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Found;
                    context.Response.RedirectLocation = "/final";
                }
                else
                {
                    var body = Encoding.UTF8.GetBytes("clip");
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    context.Response.ContentType = "video/mp4";
                    await context.Response.OutputStream.WriteAsync(body);
                }

                context.Response.Close();
            }
        });
        return (listener, $"http://127.0.0.1:{port}/", paths);
    }

    private static int FreeTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

internal static class ProviderClientInspection
{
    // Providers receive their HttpClient as a constructor dependency; reflection finds the field
    // (primary-constructor captures may have compiler-mangled names, so match by value type).
    public static HttpClient EffectiveClient(object provider)
    {
        var client = provider.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.GetValue(provider))
            .OfType<HttpClient>()
            .FirstOrDefault();
        Assert.NotNull(client);
        return client!;
    }

    public static HttpMessageHandler InnermostHandler(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handler = field!.GetValue(client) as HttpMessageHandler;
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        Assert.NotNull(handler);
        return handler!;
    }

    public static string[] Snapshot(List<string> paths)
    {
        lock (paths)
        {
            return paths.ToArray();
        }
    }

    public static void Clear(List<string> paths)
    {
        lock (paths)
        {
            paths.Clear();
        }
    }
}
