using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AccountSettingsTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AccountSettingsTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Profile_update_persists_preferences_and_returns_them_from_me()
    {
        using var client = factory.CreateClient();
        var email = $"settings-{Guid.NewGuid():N}@example.com";
        var auth = await Register(client, "Settings Owner", email);

        Assert.Equal("en", auth.User.DefaultGenerationLanguage);
        Assert.Equal("UTC", auth.User.TimeZone);
        Assert.Equal("balanced", auth.User.OutputPreference);
        Assert.True(auth.User.IncludeSourceLinks);

        var updated = await SendWithCsrf<AuthResponse>(client, HttpMethod.Patch, "/api/auth/profile", new
        {
            displayName = "Settings Owner Updated",
            preferredLanguage = "ar",
            defaultGenerationLanguage = "ku",
            timeZone = "Asia/Baghdad",
            outputPreference = "concise",
            includeSourceLinks = false,
        });

        Assert.Equal("Settings Owner Updated", updated.User.DisplayName);
        Assert.Equal("ar", updated.User.PreferredLanguage);
        Assert.Equal("ku", updated.User.DefaultGenerationLanguage);
        Assert.Equal("Asia/Baghdad", updated.User.TimeZone);
        Assert.Equal("concise", updated.User.OutputPreference);
        Assert.False(updated.User.IncludeSourceLinks);

        var current = await client.GetFromJsonAsync<AuthResponse>("/api/auth/me");
        Assert.NotNull(current);
        Assert.Equal("Asia/Baghdad", current!.User.TimeZone);
        Assert.Equal("concise", current.User.OutputPreference);
        Assert.False(current.User.IncludeSourceLinks);
    }

    [Fact]
    public async Task New_user_can_complete_onboarding_with_preferences_and_a_real_workflow_intent()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Onboarding Owner", $"onboarding-{Guid.NewGuid():N}@example.com");

        Assert.Null(auth.User.OnboardingCompletedAt);
        Assert.Null(auth.User.OnboardingIntent);

        var completed = await SendWithCsrf<AuthResponse>(client, HttpMethod.Post, "/api/auth/onboarding/complete", new
        {
            displayName = "Onboarding Owner Updated",
            preferredLanguage = "ar",
            defaultGenerationLanguage = "ku",
            intent = "research",
        });

        Assert.Equal("Onboarding Owner Updated", completed.User.DisplayName);
        Assert.Equal("ar", completed.User.PreferredLanguage);
        Assert.Equal("ku", completed.User.DefaultGenerationLanguage);
        Assert.Equal("research", completed.User.OnboardingIntent);
        Assert.NotNull(completed.User.OnboardingCompletedAt);

        var current = await client.GetFromJsonAsync<AuthResponse>("/api/auth/me");
        Assert.NotNull(current);
        Assert.Equal("research", current!.User.OnboardingIntent);
        Assert.NotNull(current.User.OnboardingCompletedAt);
    }

    [Fact]
    public async Task New_user_can_skip_onboarding_and_the_skip_persists()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Skip Owner", $"onboarding-skip-{Guid.NewGuid():N}@example.com");

        var completed = await SendWithCsrf<AuthResponse>(client, HttpMethod.Post, "/api/auth/onboarding/complete", new
        {
            preferredLanguage = "en",
            defaultGenerationLanguage = "en",
        });

        Assert.Equal(auth.User.DisplayName, completed.User.DisplayName);
        Assert.Null(completed.User.OnboardingIntent);
        Assert.NotNull(completed.User.OnboardingCompletedAt);
    }

    [Fact]
    public async Task Onboarding_completion_requires_an_authenticated_session()
    {
        using var anonymous = factory.CreateClient();
        var response = await SendWithCsrf(anonymous, HttpMethod.Post, "/api/auth/onboarding/complete", new
        {
            preferredLanguage = "en",
            defaultGenerationLanguage = "en",
            intent = "project",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Password_change_requires_current_password_and_replaces_login_secret()
    {
        using var client = factory.CreateClient();
        var email = $"password-{Guid.NewGuid():N}@example.com";
        await Register(client, "Password Owner", email);

        var wrongCurrent = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/password", new
        {
            currentPassword = "WrongPassword!123",
            newPassword = "NewStrongPassword!123",
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        var wrongBody = await wrongCurrent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CURRENT_PASSWORD_INVALID", wrongBody.GetProperty("error").GetProperty("code").GetString());

        var changed = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/password", new
        {
            currentPassword = "StrongPassword!123",
            newPassword = "NewStrongPassword!123",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        await Logout(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, email, "StrongPassword!123")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client, email, "NewStrongPassword!123")).StatusCode);
    }

    [Fact]
    public async Task Unsupported_account_preferences_are_rejected_without_persisting_partial_changes()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Validation Owner", $"settings-validation-{Guid.NewGuid():N}@example.com");

        var invalid = await SendWithCsrf(client, HttpMethod.Patch, "/api/auth/profile", new
        {
            displayName = "Validation Owner",
            preferredLanguage = "en",
            defaultGenerationLanguage = "fr",
            timeZone = "Not/A_Timezone",
            outputPreference = "verbose",
            includeSourceLinks = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var current = await client.GetFromJsonAsync<AuthResponse>("/api/auth/me");
        Assert.Equal(auth.User.Id, current!.User.Id);
        Assert.Equal("en", current.User.DefaultGenerationLanguage);
        Assert.Equal("UTC", current.User.TimeZone);
        Assert.Equal("balanced", current.User.OutputPreference);
        Assert.True(current.User.IncludeSourceLinks);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string email, string password) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/auth/login", new { email, password });

    private static async Task Logout(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
