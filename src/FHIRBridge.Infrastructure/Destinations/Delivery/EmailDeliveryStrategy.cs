using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Email;

namespace FHIRBridge.Infrastructure.Destinations.Delivery;

/// <summary>
/// Emails the generated file as an attachment and discards it — nothing is persisted. Recipient(s) and the
/// subject/body templates (with <c>{{RouteName}}</c>/<c>{{RunDate}}</c>/<c>{{RowCount}}</c> placeholders) come from
/// the destination's <c>dest_email*</c> connection-metadata fields.
/// </summary>
public sealed class EmailDeliveryStrategy : IArtifactDeliveryStrategy
{
    private const string DefaultSubjectTemplate = "Segue CSV Export - {{RouteName}} - {{RunDate}}";
    private const string DefaultBodyTemplate = "Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.";

    private readonly IEmailSender _emailSender;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly INotificationSettingsRepository _settingsRepository;

    public EmailDeliveryStrategy(
        IEmailSender emailSender, IGovernanceLogger governanceLogger, INotificationSettingsRepository settingsRepository)
    {
        _emailSender = emailSender;
        _governanceLogger = governanceLogger;
        _settingsRepository = settingsRepository;
    }

    public async Task<DestinationWriteResult> DeliverAsync(
        DestinationConfiguration destination,
        GeneratedFile file,
        int recordCount,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var metadata = destination.ConnectionMetadataJson;
        var toEmails = SplitAddresses(ConnectionMetadataReader.GetString(metadata, "dest_emailTo"));
        if (toEmails.Count == 0)
        {
            throw new InvalidOperationException(
                "This destination is configured for Email delivery but has no recipient ('dest_emailTo') configured.");
        }

        var ccEmails = SplitAddresses(ConnectionMetadataReader.GetString(metadata, "dest_emailCc"));
        var subjectTemplate = ConnectionMetadataReader.GetString(metadata, "dest_emailSubjectTemplate") ?? DefaultSubjectTemplate;
        var bodyTemplate = ConnectionMetadataReader.GetString(metadata, "dest_emailBodyTemplate") ?? DefaultBodyTemplate;

        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RouteName"] = context.RouteName,
            ["RunDate"] = context.RunStartedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'"),
            ["RowCount"] = recordCount.ToString()
        };

        var subject = EmailTemplateRenderer.Render(subjectTemplate, placeholders);
        var body = EmailTemplateRenderer.Render(bodyTemplate, placeholders);
        var recipients = string.Join(", ", ccEmails.Count == 0 ? toEmails : [.. toEmails, .. ccEmails]);
        var attachmentNames = new[] { file.FileName };
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        var fromAddress = settings?.FromAddress ?? string.Empty;

        bool sent;
        try
        {
            sent = await _emailSender.SendAsync(
                toEmails,
                ccEmails.Count == 0 ? null : ccEmails,
                subject,
                body,
                [new EmailAttachment(file.FileName, file.Content, file.ContentType)],
                cancellationToken);
        }
        catch (Exception exception)
        {
            await _governanceLogger.LogNotificationAsync(
                new NotificationEntry("Email", recipients, "Failed", subject, exception.Message, context.CorrelationId, body, attachmentNames),
                cancellationToken);
            throw;
        }

        if (!sent)
        {
            // Notification Settings has email sending disabled (or was never configured) — SmtpEmailSender no-ops
            // rather than throwing, so this destination write must surface that itself instead of reporting the
            // false "Succeeded" this write would otherwise show (no exception was ever thrown).
            const string skippedReason = "Email delivery is disabled in Notification Settings.";
            await _governanceLogger.LogNotificationAsync(
                new NotificationEntry("Email", recipients, "Skipped", subject, skippedReason, context.CorrelationId, body, attachmentNames),
                cancellationToken);
            throw new InvalidOperationException(
                $"This destination is configured for Email delivery, but {skippedReason} Enable it under Settings > " +
                "Email Settings, or switch this destination to a different delivery mode.");
        }

        await _governanceLogger.LogNotificationAsync(
            new NotificationEntry("Email", recipients, "Sent", subject, CorrelationId: context.CorrelationId, Body: body, AttachmentNames: attachmentNames),
            cancellationToken);

        return new DestinationWriteResult(
            recordCount,
            EmailDelivery: new EmailDeliveryDetail(fromAddress, toEmails, ccEmails, subject, body, attachmentNames, "Sent"));
    }

    private static IReadOnlyList<string> SplitAddresses(string? raw)
    {
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
