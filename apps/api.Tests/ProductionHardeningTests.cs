using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ProductionHardeningTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ProductionHardeningTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Health_and_readiness_are_safe_and_distinct_endpoints()
    {
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", health.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", health.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("password", await health.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var readiness = await client.GetAsync("/readiness");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        var body = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Disabled_user_is_rejected_by_cookie_validation_on_next_request()
    {
        using var client = factory.CreateClient();
        var registration = await Register(client, "Revocation Owner");
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        var auth = await registration.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var user = await db.Users.SingleAsync(item => item.Id == auth.User.Id);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Archive_paths_and_expansion_are_rejected_before_xml_parsing()
    {
        var extractor = factory.Services.GetRequiredService<IFileContentExtractor>();

        await using var unsafeArchive = new MemoryStream();
        using (var archive = new ZipArchive(unsafeArchive, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = new StreamWriter(archive.CreateEntry("../outside.xml").Open(), Encoding.UTF8, leaveOpen: false))
            await entry.WriteAsync("<root />");
        unsafeArchive.Position = 0;
        var unsafeResult = await extractor.ExtractAsync(".docx", unsafeArchive);
        Assert.Equal(FileExtractionStatus.Failed, unsafeResult.Status);
        Assert.Equal("EXTRACTION_FAILED", unsafeResult.FailureCode);

        await using var bomb = new MemoryStream();
        using (var archive = new ZipArchive(bomb, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("word/document.xml", CompressionLevel.SmallestSize);
            await using var stream = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
            await stream.WriteAsync("<document>" + new string('A', 2_000_000) + "</document>");
        }
        bomb.Position = 0;
        var bombResult = await extractor.ExtractAsync(".docx", bomb);
        Assert.Equal(FileExtractionStatus.Failed, bombResult.Status);
        Assert.Equal("EXTRACTION_FAILED", bombResult.FailureCode);
    }

    private static async Task<HttpResponseMessage> Register(HttpClient client, string name)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName = name,
                email = $"hardening-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }
}
