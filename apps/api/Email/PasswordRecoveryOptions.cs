using System.Net.Mail;

namespace Taslim.Api.Email;

public sealed class PasswordRecoveryOptions
{
    public const int PasswordResetTokenLifetimeMinutes = 60;
    // A finite lease reduces short-timeout overlap; it cannot guarantee exactly-once SMTP delivery.
    public const int SmtpSendTimeoutSeconds = 15;
    public const int LeaseProcessingMarginSeconds = 45;
    public const int MinimumLeaseSeconds = SmtpSendTimeoutSeconds + LeaseProcessingMarginSeconds;

    public bool Enabled { get; set; }
    public string PublicBaseUrl { get; set; } = string.Empty;
    public int RequestCooldownMinutes { get; set; } = 5;
    public int WorkerPollSeconds { get; set; } = 5;
    public int MaxDeliveryAttempts { get; set; } = 5;
    public int LeaseSeconds { get; set; } = 60;

    public bool IsReady(bool isProduction, IEmailSender emailSender) =>
        Enabled
        && emailSender.IsConfigured
        && RequestCooldownMinutes is >= 1 and <= 1440
        && WorkerPollSeconds is >= 1 and <= 60
        && MaxDeliveryAttempts is >= 1 and <= 10
        && LeaseSeconds is >= MinimumLeaseSeconds and <= 300
        && PasswordResetLinkBuilder.IsAllowedBaseUrl(PublicBaseUrl, isProduction);
}

public static class PasswordResetLinkBuilder
{
    public static bool IsAllowedBaseUrl(string? value, bool isProduction)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not ("/" or ""))
        {
            return false;
        }

        if (isProduction)
            return uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, "taslim.ai", StringComparison.OrdinalIgnoreCase)
                && uri.IsDefaultPort;

        return (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && (string.Equals(uri.Host, "taslim.ai", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase));
    }

    public static string Build(string publicBaseUrl, string email, string token, string language)
    {
        var resetPage = new Uri(new Uri(publicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute), "reset-password");
        var safeLanguage = language is "ar" or "ku" ? language : "en";
        // Tokens and email addresses stay in the URL fragment: browsers do not
        // send fragments in HTTP requests, Referer headers, or reverse-proxy logs.
        var fragment = string.Join("&", new[]
        {
            $"email={Uri.EscapeDataString(email)}",
            $"token={Uri.EscapeDataString(token)}",
            $"lang={Uri.EscapeDataString(safeLanguage)}",
        });
        return $"{resetPage.AbsoluteUri}#{fragment}";
    }
}

public sealed class SmtpEmailOptions
{
    public string Provider { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Taslim.ai";

    public bool IsConfigured
    {
        get
        {
            return string.Equals(Provider, "Smtp", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Host)
                && Port is >= 1 and <= 65535
                && Port != 465
                && EnableSsl
                && !string.IsNullOrWhiteSpace(Username)
                && !string.IsNullOrWhiteSpace(Password)
                && MailAddress.TryCreate(FromAddress, out _)
                && !string.IsNullOrWhiteSpace(FromName);
        }
    }
}
