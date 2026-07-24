using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Email;

namespace FHIRBridge.Infrastructure.Destinations.Delivery;

/// <summary>
/// Emails the generated file as an attachment and discards it — nothing is persisted. Recipient(s) and the
/// subject/body templates (with <c>{{RouteName}}</c>/<c>{{RunDate}}</c>/<c>{{RowCount}}</c> placeholders) come from
/// the destination's <c>dest_email*</c> connection-metadata fields.
/// </summary>
public sealed class EmailDeliveryStrategy : IArtifactDeliveryStrategy
{
    private const string DefaultSubjectTemplate = "FHIRBridge CSV Export - {{RouteName}} - {{RunDate}}";
    private const string DefaultBodyTemplate = "Attached is your requested export ({{RowCount}} record(s)), generated {{RunDate}}.";

    private readonly IEmailSender _emailSender;

    public EmailDeliveryStrategy(IEmailSender emailSender)
    {
        _emailSender = emailSender;
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

        await _emailSender.SendAsync(
            toEmails,
            ccEmails.Count == 0 ? null : ccEmails,
            EmailTemplateRenderer.Render(subjectTemplate, placeholders),
            EmailTemplateRenderer.Render(bodyTemplate, placeholders),
            [new EmailAttachment(file.FileName, file.Content, file.ContentType)],
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
