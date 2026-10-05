using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AuthBootstrapConsistencyTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AuthBootstrapConsistencyTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Login_fails_closed_without_issuing_a_session_when_personal_workspace_is_missing()
    {
        var email = $"incomplete-bootstrap-{Guid.NewGuid():N}@example.com";
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var now = DateTime.UtcNow;
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                DisplayName = "Incomplete Bootstrap",
                PreferredLanguage = LanguageCodes.English,
                CreatedAt = now,
                UpdatedAt = now,
                IsActive = true,
            };
            var result = await userManager.CreateAsync(user, "StrongPassword!123");
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
        }

        using var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new { email, password = "StrongPassword!123" });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCOUNT_SETUP_INCOMPLETE", body.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            cookie => cookie.StartsWith("taslim.auth=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
