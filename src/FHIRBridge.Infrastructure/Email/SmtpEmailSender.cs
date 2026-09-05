using System.Net;
using System.Net.Mail;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Email;

/// <summary>
/// Sends email via the global <see cref="NotificationSettings"/> row (Settings hub &gt; Email Settings) instead of
/// the "Email" appsettings.json section this replaced. Resolved fresh on every send rather than cached — outbound
/// email isn't a hot path, and this guarantees a settings change takes effect immediately without a restart.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly INotificationSettingsRepository _settingsRepository;
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(
        INotificationSettingsRepository settingsRepository,
        ISecretProvider secretProvider,
        ILogger<SmtpEmailSender> logger)
    {
        _settingsRepository = settingsRepository;
        _secretProvider = secretProvider;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        if (settings is not { IsEnabled: true })
        {
            _logger.LogInformation("Email sending is disabled; skipped '{Subject}' to {ToEmail}.", subject, toEmail);
            return false;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(toEmail);

        await SendAsync(settings, message, subject, toEmail, cancellationToken);
        return true;
    }

    public async Task<bool> SendAsync(
        IReadOnlyCollection<string> toEmails,
        IReadOnlyCollection<string>? ccEmails,
        string subject,
        string htmlBody,
        IReadOnlyCollection<EmailAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var recipients = string.Join(", ", toEmails);
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        if (settings is not { IsEnabled: true })
        {
            _logger.LogInformation("Email sending is disabled; skipped '{Subject}' to {ToEmails}.", subject, recipients);
            return false;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
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

        await SendAsync(settings, message, subject, recipients, cancellationToken);
        return true;
    }

    private async Task SendAsync(
        NotificationSettings settings,
        MailMessage message,
        string subject,
        string recipients,
        CancellationToken cancellationToken)
    {
        var password = settings.PasswordSecretReference is { } passwordReference
            ? await _secretProvider.GetSecretAsync(passwordReference, cancellationToken)
            : string.Empty;

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            Credentials = new NetworkCredential(settings.Username, password)
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
