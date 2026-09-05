using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace FHIRBridge.Infrastructure.Email;

/// <summary>
/// Sends email via the global <see cref="NotificationSettings"/> row (Settings hub &gt; Email Settings) instead of
/// the "Email" appsettings.json section this replaced. Resolved fresh on every send rather than cached — outbound
/// email isn't a hot path, and this guarantees a settings change takes effect immediately without a restart.
///
/// Built on MailKit's <see cref="MailKit.Net.Smtp.SmtpClient"/>, not the obsolete System.Net.Mail.SmtpClient it
/// replaced — the legacy client has a long-documented failure mode against real-world relays (Brevo, SendGrid,
/// Office365, Gmail) over STARTTLS on port 587: it silently skips AUTH negotiation and only surfaces the failure
/// later, as "5.7.0 Please authenticate first" on MAIL FROM, rather than as an authentication error. MailKit
/// implements AUTH negotiation (LOGIN/PLAIN) correctly against these providers.
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

        var message = new MimeMessage
        {
            Subject = subject,
        };
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

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

        var message = new MimeMessage
        {
            Subject = subject,
        };
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));

        foreach (var toEmail in toEmails)
        {
            message.To.Add(MailboxAddress.Parse(toEmail));
        }

        foreach (var ccEmail in ccEmails ?? [])
        {
            message.Cc.Add(MailboxAddress.Parse(ccEmail));
        }

        var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
        foreach (var attachment in attachments ?? [])
        {
            bodyBuilder.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }
        message.Body = bodyBuilder.ToMessageBody();

        await SendAsync(settings, message, subject, recipients, cancellationToken);
        return true;
    }

    private async Task SendAsync(
        NotificationSettings settings,
        MimeMessage message,
        string subject,
        string recipients,
        CancellationToken cancellationToken)
    {
        var password = settings.PasswordSecretReference is { } passwordReference
            ? await _secretProvider.GetSecretAsync(passwordReference, cancellationToken)
            : string.Empty;

        using var client = new SmtpClient
        {
            // Many networks (corporate proxies, sandboxed/containerized egress) block outbound OCSP/CRL
            // revocation lookups entirely, which otherwise makes .NET reject an otherwise-valid, correctly
            // issued/hostnamed/unexpired certificate purely because revocation status couldn't be checked —
            // this only skips that specific check; the rest of the chain (issuer, hostname, expiry) is still
            // fully validated.
            CheckCertificateRevocation = false,
        };
        try
        {
            // Auto: SslOnConnect for port 465 (implicit TLS), StartTlsWhenAvailable otherwise (port 587/25) —
            // matches how settings.EnableSsl was previously interpreted by System.Net.Mail.SmtpClient, without
            // needing a separate "connection mode" setting for the two common conventions.
            var secureSocketOptions = settings.EnableSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None;
            await client.ConnectAsync(settings.Host, settings.Port, secureSocketOptions, cancellationToken);
            if (!string.IsNullOrEmpty(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, password, cancellationToken);
            }
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            _logger.LogInformation("Email '{Subject}' sent to {ToEmails}.", subject, recipients);
        }
        // Broad on purpose: MailKit surfaces failures as several distinct types (SmtpCommandException,
        // SmtpProtocolException, MailKit.Security.AuthenticationException, MailKit.Security.SslHandshakeException,
        // plain socket/IO errors for an unreachable host) — all of them are "the send failed", and should be
        // logged with subject/recipient context and rethrown, same as the single SmtpException catch this
        // replaced. OperationCanceledException is excluded — that's a deliberate cancellation, not a send failure.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send email '{Subject}' to {ToEmails}.", subject, recipients);
            throw;
        }
    }
}
