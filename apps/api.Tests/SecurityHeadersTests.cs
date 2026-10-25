using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SecurityHeadersTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public SecurityHeadersTests(TaslimApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Csrf_rejection_retains_security_headers_after_response_clear()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Missing CSRF",
                email = $"missing-csrf-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("payment=()", response.Headers.GetValues("Permissions-Policy").Single());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CSRF_VALIDATION_FAILED", body.GetProperty("error").GetProperty("code").GetString());
    }
}
