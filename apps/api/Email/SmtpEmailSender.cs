using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Taslim.Api.Email;

public sealed class SmtpEmailSender(IOptionsMonitor<SmtpEmailOptions> options) : IEmailSender
{
    private const int SendTimeoutMilliseconds = 15_000;

    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task SendAsync(string recipient, string subject, string plainTextBody, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Password recovery email delivery is not configured.");

        cancellationToken.ThrowIfCancellationRequested();
        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = subject,
            Body = plainTextBody,
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(recipient));

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Timeout = SendTimeoutMilliseconds,
        };
        if (!string.IsNullOrWhiteSpace(settings.Username))
            client.Credentials = new NetworkCredential(settings.Username, settings.Password);

        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sendTimeout.CancelAfter(SendTimeoutMilliseconds);
        try
        {
            await client.SendMailAsync(message, sendTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("SMTP delivery timed out.");
        }
    }
}
