using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Notifications;
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

    public EmailDeliveryStrategy(IEmailSender emailSender, IGovernanceLogger governanceLogger)
    {
        _emailSender = emailSender;
        _governanceLogger = governanceLogger;
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
        var recipients = string.Join(", ", ccEmails.Count == 0 ? toEmails : [.. toEmails, .. ccEmails]);

        try
        {
            await _emailSender.SendAsync(
                toEmails,
                ccEmails.Count == 0 ? null : ccEmails,
                subject,
                EmailTemplateRenderer.Render(bodyTemplate, placeholders),
                [new EmailAttachment(file.FileName, file.Content, file.ContentType)],
                cancellationToken);
        }
        catch (Exception exception)
        {
            await _governanceLogger.LogNotificationAsync(
                new NotificationEntry("Email", recipients, "Failed", subject, exception.Message, context.CorrelationId),
                cancellationToken);
            throw;
        }

        await _governanceLogger.LogNotificationAsync(
            new NotificationEntry("Email", recipients, "Sent", subject, CorrelationId: context.CorrelationId),
            cancellationToken);

        return new DestinationWriteResult(recordCount);
    }

    private static IReadOnlyList<string> SplitAddresses(string? raw)
    {
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
