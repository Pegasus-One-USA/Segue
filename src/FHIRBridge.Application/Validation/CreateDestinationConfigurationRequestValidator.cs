using System.Text.Json;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FluentValidation;

namespace FHIRBridge.Application.Validation;

/// <summary>
/// Server-side mirror of the Angular destination wizard's sqlForm/mongoForm/csvForm required fields and
/// <c>_syncDeliveryModeValidators</c>'s conditional-by-delivery-mode rules (destination-wizard.component.ts).
/// <see cref="CreateDestinationConfigurationRequest.ConnectionMetadataJson"/> is an opaque flat JSON blob —
/// there is no strongly-typed DTO per <see cref="DestinationType"/> — so this dispatches on
/// <see cref="CreateDestinationConfigurationRequest.DestinationType"/> and inspects the parsed dictionary
/// directly rather than adding a new typed contract the rest of the pipeline doesn't use.
/// A null <see cref="CreateDestinationConfigurationRequest.ConnectionMetadataJson"/> means "preserve whatever
/// is already saved" on an update (see <c>ConfigurationService.UpdateDestinationConfigurationAsync</c>), so
/// metadata-shape rules are skipped entirely in that case — only the base fields still apply.
/// </summary>
public sealed class CreateDestinationConfigurationRequestValidator : AbstractValidator<CreateDestinationConfigurationRequest>
{
    private static readonly DestinationType[] SqlFamily =
    [
        DestinationType.SqlServer,
        DestinationType.AzureSql,
        DestinationType.MySql,
        DestinationType.PostgreSql,
    ];

    public CreateDestinationConfigurationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.KeyVaultName).NotEmpty();
        RuleFor(x => x.SecretName).NotEmpty();

        RuleFor(x => x).Custom(ValidateConnectionMetadata);
    }

    private static void ValidateConnectionMetadata(
        CreateDestinationConfigurationRequest request,
        ValidationContext<CreateDestinationConfigurationRequest> context)
    {
        if (request.ConnectionMetadataJson is null)
        {
            return;
        }

        var metadata = ParseMetadata(request.ConnectionMetadataJson);

        if (SqlFamily.Contains(request.DestinationType))
        {
            RequireField(context, metadata, "dest_server", "Server is required.");
            RequireField(context, metadata, "dest_database", "Database is required.");
            RequireField(context, metadata, "dest_auth", "Authentication mode is required.");
        }
        else if (request.DestinationType == DestinationType.Mongo)
        {
            RequireField(context, metadata, "dest_collection", "Collection is required.");
        }
        else if (request.DestinationType == DestinationType.Csv)
        {
            ValidateCsvMetadata(context, metadata);
        }
        else if (request.DestinationType == DestinationType.FhirRepository)
        {
            ValidateFhirRepositoryMetadata(request, context, metadata);
        }
    }

    private static readonly string[] SupportedFhirAuthTypes = ["none", "bearer", "basic", "clientCredentials"];

    private static void ValidateFhirRepositoryMetadata(
        CreateDestinationConfigurationRequest request,
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata)
    {
        var authType = metadata.GetValueOrDefault("dest_fhirAuthType", "none");

        if (!SupportedFhirAuthTypes.Contains(authType, StringComparer.OrdinalIgnoreCase))
        {
            context.AddFailure(
                "dest_fhirAuthType",
                $"Unsupported FHIR auth type '{authType}'. Supported values: none, bearer, basic, clientCredentials.");
            return;
        }

        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase))
        {
            // Matches today's (absence of) validation for FhirRepository exactly — no new required fields.
            return;
        }

        // Auth is opt-in: once enabled, Target must carry the plain FHIR base URL, because the secret is
        // repurposed to hold auth material instead (see FhirRepositoryAuthResolver in FHIRBridge.Infrastructure).
        if (string.IsNullOrWhiteSpace(request.Target))
        {
            context.AddFailure("Target", "Target (FHIR base URL) is required when dest_fhirAuthType is not 'none'.");
        }
    }

    private static void ValidateCsvMetadata(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata)
    {
        RequireField(context, metadata, "dest_filePattern", "File pattern is required.");

        var deliveryMode = metadata.GetValueOrDefault("dest_deliveryMode", "download");
        switch (deliveryMode)
        {
            case "sftp":
                RequireField(context, metadata, "dest_sftpHost", "SFTP host is required.");
                RequireField(context, metadata, "dest_sftpUsername", "SFTP username is required.");
                RequireField(context, metadata, "dest_sftpRemoteFolder", "SFTP remote folder is required.");
                RequireIntInRange(
                    context, metadata, "dest_sftpPort", 1, 65535, "SFTP port must be between 1 and 65535.");
                break;
            case "email":
                RequireField(context, metadata, "dest_emailTo", "Recipient email is required.");
                break;
            case "downloadUrl":
                RequireIntInRange(
                    context,
                    metadata,
                    "dest_downloadLinkExpiryMinutes",
                    1,
                    10080,
                    "Download link expiry must be between 1 and 10080 minutes.");
                break;
        }
    }

    private static void RequireField(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string message)
    {
        if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            context.AddFailure(key, message);
        }
    }

    private static void RequireIntInRange(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata,
        string key,
        int min,
        int max,
        string message)
    {
        if (!metadata.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, out var value) ||
            value < min ||
            value > max)
        {
            context.AddFailure(key, message);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseMetadata(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
