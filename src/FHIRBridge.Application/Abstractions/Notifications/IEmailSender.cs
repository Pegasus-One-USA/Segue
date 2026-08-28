namespace FHIRBridge.Application.Abstractions.Notifications;

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

public interface IEmailSender
{
    /// <returns><c>true</c> if the message was actually handed to SMTP; <c>false</c> if delivery was skipped
    /// because email sending is disabled or unconfigured in Notification Settings.</returns>
    Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken);

    /// <returns><c>true</c> if the message was actually handed to SMTP; <c>false</c> if delivery was skipped
    /// because email sending is disabled or unconfigured in Notification Settings.</returns>
    Task<bool> SendAsync(
        IReadOnlyCollection<string> toEmails,
        IReadOnlyCollection<string>? ccEmails,
        string subject,
        string htmlBody,
        IReadOnlyCollection<EmailAttachment>? attachments,
        CancellationToken cancellationToken);
}
