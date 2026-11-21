namespace Taslim.Api.Email;

public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendAsync(string recipient, string subject, string plainTextBody, CancellationToken cancellationToken);
}
