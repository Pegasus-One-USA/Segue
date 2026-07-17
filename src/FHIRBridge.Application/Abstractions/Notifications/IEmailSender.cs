namespace FHIRBridge.Application.Abstractions.Notifications;

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken);

    Task SendAsync(
        IReadOnlyCollection<string> toEmails,
        IReadOnlyCollection<string>? ccEmails,
        string subject,
        string htmlBody,
        IReadOnlyCollection<EmailAttachment>? attachments,
        CancellationToken cancellationToken);
}
