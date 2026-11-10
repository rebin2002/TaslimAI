using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Domain;
using Taslim.Api.Email;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Provider-free regression coverage for the localized forgot-password UI and reset-token
// expiry contract on the #686 recovery lane. All mail flows through the deterministic
// RecordingEmailSender fake; no SMTP socket, real mailbox, secret, or provider is used.
public sealed class PasswordRecoveryTokenExpiryLocalizationTests
{
    private const string InitialPassword = "StrongPassword!123";
    private const string ReplacementPassword = "EvenStronger!123";
    private const string CanonicalInvalidResetMessage = "This reset link is invalid or expired. Request a new one.";

    [Fact]
    public void Localized_reset_emails_advertise_the_server_enforced_expiry_window()
    {
        // The canonical expiry window in code and in every supported recipient language
        // must agree, so a user reading an English, Arabic, or Kurdish email is never
        // told a different lifetime than the one the redemption endpoint enforces.
        Assert.Equal(60, PasswordRecoveryOptions.PasswordResetTokenLifetimeMinutes);

        var english = PasswordResetEmailTemplate.Create("en", "http://localhost:3000/reset-password#email=a%40b.test&token=x&lang=en");
        Assert.Contains("within one hour", english.Body, StringComparison.Ordinal);

        var arabic = PasswordResetEmailTemplate.Create("ar", "http://localhost:3000/reset-password#email=a%40b.test&token=x&lang=ar");
        Assert.Contains("خلال ساعة", arabic.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("within one hour", arabic.Body, StringComparison.Ordinal);

        var kurdish = PasswordResetEmailTemplate.Create("ku", "http://localhost:3000/reset-password#email=a%40b.test&token=x&lang=ku");
        Assert.Contains("لە ماوەی یەک کاتژمێردا", kurdish.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("within one hour", kurdish.Body, StringComparison.Ordinal);

        // Unsupported or missing account language must not invent an unknown locale:
        // delivery falls back to the canonical English copy.
        foreach (var language in new string?[] { "zh", "fr", "", null })
        {
            var fallback = PasswordResetEmailTemplate.Create(language, "http://localhost:3000/reset-password#email=a%40b.test&token=x&lang=en");
            Assert.Equal(english.Subject, fallback.Subject);
            Assert.Equal(english.Body, fallback.Body);
        }
    }

    [Fact]
    public void Reset_links_keep_expiry_credentials_in_the_fragment_and_carry_the_recipient_locale()
    {
        const string baseUrl = "http://localhost:3000";
        const string email = "expiry+locale@example.test";
        const string token = "synthetic.token-with-symbols&=?#";

        var link = PasswordResetLinkBuilder.Build(baseUrl, email, token, "ku");
        Assert.StartsWith("http://localhost:3000/reset-password#", link, StringComparison.Ordinal);
        // Credentials travel only in the fragment: browsers never attach it to requests,
        // Referer headers, or proxy logs, which is what keeps the expiry link non-leaking.
        Assert.DoesNotContain("?", link, StringComparison.Ordinal);

        var fragment = ReadFragment(link);
        Assert.Equal(email, fragment["email"]);
        Assert.Equal(token, fragment["token"]);
        Assert.Equal("ku", fragment["lang"]);

        // A lost or invalid account locale downgrades the link to English rather than
        // rendering an unknown language tag into the reset UI deep link.
        Assert.Equal("en", ReadFragment(PasswordResetLinkBuilder.Build(baseUrl, email, token, "sx"))["lang"]);
        Assert.Equal("en", ReadFragment(PasswordResetLinkBuilder.Build(baseUrl, email, token, string.Empty))["lang"]);

        // The isolated staging hostname is deliberately still rejected by the origin
        // policy, so a synthetic link can never be built for an unapproved host while
        // loopback development origins remain available outside Production.
        Assert.False(PasswordResetLinkBuilder.IsAllowedBaseUrl("https://staging.taslim.ai", isProduction: false));
        Assert.False(PasswordResetLinkBuilder.IsAllowedBaseUrl("https://staging.taslim.ai", isProduction: true));
        Assert.False(PasswordResetLinkBuilder.IsAllowedBaseUrl("https://taslim.ai.evil.test", isProduction: true));
        Assert.True(PasswordResetLinkBuilder.IsAllowedBaseUrl(baseUrl, isProduction: false));
    }

    [Fact]
    public async Task Localized_reset_email_link_redeems_then_the_consumed_attempt_is_generic_and_non_enumerating()
    {
        using var factory = new PasswordRecoveryApiFactory();
        var email = $"expiry-flow-{Guid.NewGuid():N}@example.test";
        using var registration = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(registration, email, "ar")).StatusCode);

        using var requestClient = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(requestClient, email)).StatusCode);
        var delivered = await WaitForEmailAsync(factory.EmailSender, email);
        Assert.Equal(email, delivered.Recipient, ignoreCase: true);

        var values = ReadFragment(ExtractResetUrl(delivered.Body));
        Assert.Equal("ar", values["lang"]);
        Assert.Contains("خلال ساعة", delivered.Body, StringComparison.Ordinal);
        // The captured message must never advertise the transport it will be sent over.
        Assert.DoesNotContain("smtp", delivered.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smtp", delivered.Subject, StringComparison.OrdinalIgnoreCase);

        using var firstReset = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(firstReset, email, values["token"], ReplacementPassword)).StatusCode);
        Assert.True(await CheckPasswordAsync(factory, email, ReplacementPassword));

        // Replaying the consumed link is refused with the canonical generic expiry copy;
        // it is indistinguishable from an unknown account so expiry failures cannot
        // enumerate identities.
        using var replay = factory.CreateClient();
        using var replayed = await ResetAsync(replay, email, values["token"], "AnotherStronger!456");
        using var unknownAccount = factory.CreateClient();
        using var unknown = await ResetAsync(unknownAccount, $"absent-{Guid.NewGuid():N}@example.test", values["token"], "AnotherStronger!456");
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var replayBody = await replayed.Content.ReadAsStringAsync();
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), replayBody);
        var replayJson = JsonDocument.Parse(replayBody).RootElement;
        Assert.Equal("PASSWORD_RESET_INVALID", replayJson.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(CanonicalInvalidResetMessage, replayJson.GetProperty("error").GetProperty("message").GetString());

        // One queued request produced exactly one captured delivery and the account
        // cooldown keeps later requests generic without minting extra links.
        Assert.Single(factory.EmailSender.Messages, message => string.Equals(message.Recipient, email, StringComparison.OrdinalIgnoreCase));
        using var throttledClient = factory.CreateClient();
        using var throttled = await ForgotAsync(throttledClient, email);
        Assert.Equal(HttpStatusCode.Accepted, throttled.StatusCode);
        Assert.Equal(delivered.Body, (await WaitForSingleMessageAsync(factory.EmailSender, email)).Body, ignoreCase: false);
        Assert.Single(factory.EmailSender.Messages, message => string.Equals(message.Recipient, email, StringComparison.OrdinalIgnoreCase));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            var entry = await db.PasswordResetEmailOutbox.SingleAsync(entry => entry.UserId == user!.Id);
            Assert.Equal(PasswordResetDeliveryStates.Delivered, entry.Status);
        }
    }

    [Fact]
    public async Task Actually_expired_localized_link_fails_closed_without_touching_credentials_or_sessions()
    {
        using var factory = new PasswordRecoveryApiFactory(resetTokenLifetime: TimeSpan.FromSeconds(3));
        var email = $"expired-link-{Guid.NewGuid():N}@example.test";
        using var registration = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(registration, email, "ku")).StatusCode);

        using var requestClient = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(requestClient, email)).StatusCode);
        var delivered = await WaitForEmailAsync(factory.EmailSender, email);
        var values = ReadFragment(ExtractResetUrl(delivered.Body));
        Assert.Equal("ku", values["lang"]);
        Assert.Contains("لە ماوەی یەک کاتژمێردا", delivered.Body, StringComparison.Ordinal);
        Assert.True(await IsResetTokenValidAsync(factory, email, values["token"]));

        await Task.Delay(TimeSpan.FromMilliseconds(3_100));

        using var resetClient = factory.CreateClient();
        using var expired = await ResetAsync(resetClient, email, values["token"], ReplacementPassword);
        var expiredBody = await expired.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        var expiredJson = JsonDocument.Parse(expiredBody).RootElement;
        Assert.Equal("PASSWORD_RESET_INVALID", expiredJson.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(CanonicalInvalidResetMessage, expiredJson.GetProperty("error").GetProperty("message").GetString());
        // A refused expired link never authenticates the caller and never rotates anything.
        Assert.DoesNotContain(
            expired.Headers.TryGetValues("Set-Cookie", out var expiredCookies) ? expiredCookies : Enumerable.Empty<string>(),
            cookie => cookie.StartsWith("taslim.auth=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Unauthorized, (await resetClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.False(await CheckPasswordAsync(factory, email, ReplacementPassword));
        Assert.True(await CheckPasswordAsync(factory, email, InitialPassword));
    }

    [Fact]
    public void Forgot_password_ui_publishes_the_link_expired_recovery_state_in_every_locale()
    {
        // Contract test for the localized forgot-password UI: every supported locale
        // renders the canonical link-expired outcome and offers a way back into the
        // request flow, so an expired token is never a dead end or an untranslated
        // developer string.
        var i18n = ReadWebFile("src/lib/i18n.ts");
        var form = ReadWebFile("src/components/PasswordRecoveryForm.tsx");
        var loginLink = ReadWebFile("src/components/AuthForm.tsx");

        Assert.Equal(3, CountOccurrences(i18n, "\"auth.resetInvalid\":"));
        Assert.Contains("\"auth.resetInvalid\": \"This reset link is invalid or expired. Request a new one.\"", i18n, StringComparison.Ordinal);
        Assert.Contains("\"auth.resetInvalid\": \"رابط إعادة التعيين غير صالح أو منتهي الصلاحية. اطلب رابطًا جديدًا.\"", i18n, StringComparison.Ordinal);
        Assert.Contains("\"auth.resetInvalid\": \"بەستەری ڕێکخستنەوە نادروستە یان بەسەرچووە. بەستەرێکی نوێ داوا بکە.\"", i18n, StringComparison.Ordinal);

        Assert.Equal(3, CountOccurrences(i18n, "\"auth.forgotPassword\":"));
        Assert.Contains("\"auth.forgotEyebrow\":", i18n, StringComparison.Ordinal);
        Assert.Contains("\"auth.resetLinkMissing\":", i18n, StringComparison.Ordinal);

        // The form maps the API's canonical expiry failure onto the localized state and
        // links back to the request flow; the login page advertises the entry point.
        Assert.Contains("PASSWORD_RESET_INVALID", form, StringComparison.Ordinal);
        Assert.Contains("auth.resetInvalid", form, StringComparison.Ordinal);
        Assert.Contains("auth.resetLinkMissing", form, StringComparison.Ordinal);
        Assert.Contains("href=\"/forgot-password\"", form, StringComparison.Ordinal);
        Assert.Contains("auth.forgotPassword", loginLink, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ReadFragment(string link)
    {
        var marker = link.IndexOf('#', StringComparison.Ordinal);
        Assert.True(marker >= 0, "The reset link must carry its credentials in the URL fragment.");
        return link[(marker + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty),
                StringComparer.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string ReadWebFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "apps", "web")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "apps", "web", relativePath);
        Assert.True(File.Exists(path), $"Missing frontend contract file: {path}");
        return File.ReadAllText(path);
    }

    private static string ExtractResetUrl(string body) => body
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .First(line => line.Contains("/reset-password#", StringComparison.Ordinal));

    private static async Task<CapturedEmail> WaitForEmailAsync(RecordingEmailSender sender, string email)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end)
        {
            var message = sender.Messages.FirstOrDefault(candidate => string.Equals(candidate.Recipient, email, StringComparison.OrdinalIgnoreCase));
            if (message is not null) return message;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("Timed out waiting for the deterministic fake sender to capture the reset message.");
    }

    private static async Task<CapturedEmail> WaitForSingleMessageAsync(RecordingEmailSender sender, string email)
    {
        var message = await WaitForEmailAsync(sender, email);
        Assert.Single(sender.Messages, candidate => string.Equals(candidate.Recipient, email, StringComparison.OrdinalIgnoreCase));
        return message;
    }

    private static async Task<bool> CheckPasswordAsync(PasswordRecoveryApiFactory factory, string email, string password)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return await userManager.CheckPasswordAsync(user!, password);
    }

    private static async Task<bool> IsResetTokenValidAsync(PasswordRecoveryApiFactory factory, string email, string token)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        return await userManager.VerifyUserTokenAsync(
            user!,
            userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose,
            token);
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string preferredLanguage) =>
        SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Recovery Expiry Localization User",
            email,
            password = InitialPassword,
            preferredLanguage,
        });

    private static Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email) =>
        SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/password/forgot", new { email });

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client, string email, string token, string newPassword) =>
        SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/password/reset", new { email, token, newPassword });

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
