using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PasswordResetTokenReusedBoundaryTests
{
    [Fact]
    public async Task Consumed_token_cannot_be_reused_and_invalid_reset_does_not_enumerate_accounts()
    {
        using var factory = new PasswordRecoveryApiFactory();
        var email = $"token-reuse-{Guid.NewGuid():N}@example.test";
        const string oldPassword = "StrongPassword!123";
        const string newPassword = "EvenStronger!123";

        using var registrationClient = factory.CreateClient();
        using var registration = await RegisterAsync(registrationClient, email, oldPassword);
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            token = await userManager.GeneratePasswordResetTokenAsync(user!);
        }

        // Issue a local Identity token directly so this boundary test never
        // queues or sends email, even through the recording test transport.
        using var resetClient = factory.CreateClient();
        using var firstUse = await ResetAsync(resetClient, email, token, newPassword);
        Assert.Equal(HttpStatusCode.OK, firstUse.StatusCode);

        using var replayClient = factory.CreateClient();
        using var replay = await ResetAsync(replayClient, email, token, "AnotherStrong!456");
        using var unknownAccountClient = factory.CreateClient();
        using var unknownAccount = await ResetAsync(
            unknownAccountClient,
            $"missing-{Guid.NewGuid():N}@example.test",
            token,
            "AnotherStrong!456");

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownAccount.StatusCode);
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.Equal(replayBody, await unknownAccount.Content.ReadAsStringAsync());
        Assert.Equal(
            "PASSWORD_RESET_INVALID",
            JsonDocument.Parse(replayBody).RootElement.GetProperty("error").GetProperty("code").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.False(await userManager.CheckPasswordAsync(user!, oldPassword));
            Assert.True(await userManager.CheckPasswordAsync(user!, newPassword));
        }

        Assert.Empty(factory.EmailSender.Messages);
    }

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string password) =>
        await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Password Reset Boundary User",
            email,
            password,
            preferredLanguage = "en",
        });

    private static async Task<HttpResponseMessage> ResetAsync(HttpClient client, string email, string token, string newPassword) =>
        await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/password/reset", new { email, token, newPassword });

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }
}
