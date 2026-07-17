using System.Net;
using System.Net.Mail;
using FHIRBridge.Application.Abstractions.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Email;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Email sending is disabled; skipped '{Subject}' to {ToEmail}.", subject, toEmail);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(toEmail);

        await SendAsync(message, subject, toEmail, cancellationToken);
    }

    public async Task SendAsync(
        IReadOnlyCollection<string> toEmails,
        IReadOnlyCollection<string>? ccEmails,
        string subject,
        string htmlBody,
        IReadOnlyCollection<EmailAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var recipients = string.Join(", ", toEmails);
        if (!_options.Enabled)
        {
            _logger.LogInformation("Email sending is disabled; skipped '{Subject}' to {ToEmails}.", subject, recipients);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        foreach (var toEmail in toEmails)
        {
            message.To.Add(toEmail);
        }

        foreach (var ccEmail in ccEmails ?? [])
        {
            message.CC.Add(ccEmail);
        }

        // MailMessage.Dispose() disposes every Attachment, which in turn disposes the stream it was constructed
        // with, so no separate stream cleanup is needed here.
        foreach (var attachment in attachments ?? [])
        {
            message.Attachments.Add(new Attachment(new MemoryStream(attachment.Content), attachment.FileName, attachment.ContentType));
        }

        await SendAsync(message, subject, recipients, cancellationToken);
    }

    private async Task SendAsync(MailMessage message, string subject, string recipients, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = new NetworkCredential(_options.Username, _options.Password)
        };

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Email '{Subject}' sent to {ToEmails}.", subject, recipients);
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex, "Failed to send email '{Subject}' to {ToEmails}.", subject, recipients);
            throw;
        }
    }
}
