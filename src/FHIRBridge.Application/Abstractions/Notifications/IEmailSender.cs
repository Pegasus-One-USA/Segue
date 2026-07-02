namespace FHIRBridge.Application.Abstractions.Notifications;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken);
}
