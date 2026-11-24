using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Files;
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
// IProviderUrlPolicy has classified it. Provider tests inject stub primary handlers, so the
// real registrations were never exercised in CI; this reads the booted application's named
// HttpClientFactoryOptions and runs its ConfigurePrimaryHttpMessageHandler actions through a
// public HttpMessageHandlerBuilder probe. ManusMovieVideoProvider and
// StableAudioMusicGenerationProvider are the correction; DirectVideoHttpProviderAdapter,
// RunwayMovieVideoProvider and MubertMusicGenerationProvider are positive controls for the
// lane convention that already existed.
public sealed class ProviderDownloadRedirectHardeningTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ProviderDownloadRedirectHardeningTests(TaslimApiFactory factory) => this.factory = factory;

    public static TheoryData<Type> GuardedClientTypes { get; } = new()
    {
        typeof(Taslim.Api.Movies.ManusMovieVideoProvider),
        typeof(Taslim.Api.Music.StableAudioMusicGenerationProvider),
        typeof(Taslim.Api.Movies.DirectVideoHttpProviderAdapter),
        typeof(Taslim.Api.Movies.RunwayMovieVideoProvider),
        typeof(Taslim.Api.Music.MubertMusicGenerationProvider),
    };

    [Theory]
    [MemberData(nameof(GuardedClientTypes))]
    public void Guarded_provider_client_never_autofollows_unclassified_redirect_hops(Type clientType)
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
            // The lane registers a plain HttpClientHandler instance; anything else is a
            // convention change and must be reviewed as such rather than silently accepted.
            var handler = Assert.IsType<System.Net.Http.HttpClientHandler>(primary);
            Assert.False(handler.AllowAutoRedirect,
                $"{clientType.Name}: AllowAutoRedirect must be false; a redirect destination is provider response data and is only safe once IProviderUrlPolicy has classified it");
        }
        finally
        {
            primary!.Dispose();
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
}
