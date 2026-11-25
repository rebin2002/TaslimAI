using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// End-to-end surface of the academic template catalog: anonymous requests are rejected on every
/// route, and a signed-in studio session sees the versioned catalog, the facet-filtered
/// configurations, the synthetic preview and the honest "unknown configuration" 404. The raw
/// response bodies are scanned for backend-identity tokens exactly like the DTO-level guard tests,
/// so an accidental leak at the serialization layer fails here too.
/// </summary>
public class AcademicTemplatesApiTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory _factory;

    public AcademicTemplatesApiTests(TaslimApiFactory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Catalog_requires_authentication()
    {
        using var client = _factory.CreateClient();
        foreach (var path in new[]
        {
            "/api/academic-templates",
            "/api/academic-templates/configurations",
            "/api/academic-templates/presets",
            "/api/academic-templates/configurations/classic-thesis.times.150.classic.center",
            "/api/academic-templates/configurations/classic-thesis.times.150.classic.center/preview",
        })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Signed_in_session_sees_catalog_configurations_and_previews()
    {
        using var client = _factory.CreateClient();
        await RegisterAsync(client);

        var catalogResponse = await client.GetAsync("/api/academic-templates");
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        var catalogJson = await catalogResponse.Content.ReadAsStringAsync();
        var catalog = JsonDocument.Parse(catalogJson);
        Assert.Equal(22, catalog.RootElement.GetProperty("baseTemplateCount").GetInt32());
        Assert.True(catalog.RootElement.GetProperty("distinctConfigurationCount").GetInt32() >= 200);

        var filtered = await client.GetAsync("/api/academic-templates/configurations?language=ckb&limit=5");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var filteredJson = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        Assert.True(filteredJson.RootElement.GetProperty("total").GetInt32() >= 10);
        Assert.Equal(5, filteredJson.RootElement.GetProperty("items").GetArrayLength());

        var preview = await client.GetAsync("/api/academic-templates/configurations/kurdish-thesis-ckb.naskh.latin.150/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = await preview.Content.ReadAsStringAsync();
        Assert.Contains("dir=\\\"rtl\\\"", previewBody, StringComparison.Ordinal);
        Assert.DoesNotContain("openai", previewBody, StringComparison.OrdinalIgnoreCase);

        var missing = await client.GetAsync("/api/academic-templates/configurations/no-such-config");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("ACADEMIC_TEMPLATE_NOT_FOUND", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Catalog_has_no_write_endpoints_and_no_leakable_fields()
    {
        using var client = _factory.CreateClient();
        await RegisterAsync(client);
        // No write route exists; and the global CSRF gate answers anonymous mutations with 400,
        // so the only acceptable outcomes here are non-success. A 2xx would mean a mutation path.
        var post = await client.PostAsync("/api/academic-templates/configurations",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.False(post.IsSuccessStatusCode);

        var catalogRaw = await (await client.GetAsync("/api/academic-templates")).Content.ReadAsStringAsync();
        var presetsRaw = await (await client.GetAsync("/api/academic-templates/presets")).Content.ReadAsStringAsync();
        foreach (var token in new[] { "openai", "gpt", "claude", "provider", "billing", "credits", "endpoint", "routing" })
        {
            Assert.DoesNotContain(token, catalogRaw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, presetsRaw, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task RegisterAsync(HttpClient client)
    {
        var csrfResponse = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        var token = csrfResponse.GetProperty("token").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    displayName = "Academic Catalog Tester",
                    email = $"academic-catalog-{Guid.NewGuid():N}@example.com",
                    password = "StrongPassword!123",
                    preferredLanguage = "en",
                }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
