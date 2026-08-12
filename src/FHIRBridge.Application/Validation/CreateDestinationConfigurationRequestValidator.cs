using System.Text.Json;
using System.Text.RegularExpressions;
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
        else if (request.DestinationType == DestinationType.BlobStorage)
        {
            ValidateBlobMetadata(context, metadata);
        }
    }

    /// <summary>
    /// Azure Blob container naming rules: 3-63 characters, lowercase letters/digits/hyphens only, must start and
    /// end with a letter or digit, no consecutive hyphens. A name violating this is accepted by this API but
    /// rejected by Azure itself with an opaque "InvalidResourceName" error at write time — catching it here
    /// gives the wizard an inline, actionable error instead.
    /// </summary>
    private static readonly Regex BlobContainerNameRegex = new(
        @"^(?!.*--)[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.Compiled);

    private static void ValidateBlobMetadata(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata)
    {
        RequireField(context, metadata, "dest_blobAuthMode", "Authentication mode is required.");
        RequireField(context, metadata, "dest_blobContainer", "Container name is required.");
        RequirePattern(
            context,
            metadata,
            "dest_blobContainer",
            BlobContainerNameRegex,
            "Container name must be 3-63 characters: lowercase letters, numbers, and single hyphens only "
                + "(no leading, trailing, or double hyphens).");

        var authMode = metadata.GetValueOrDefault("dest_blobAuthMode", "connectionString");
        switch (authMode)
        {
            case "accountKey":
                RequireField(context, metadata, "dest_blobAccountName", "Account name is required.");
                break;
            case "managedIdentity":
                RequireField(context, metadata, "dest_blobAccountUrl", "Account URL is required.");
                break;
            case "servicePrincipal":
                RequireField(context, metadata, "dest_blobAccountUrl", "Account URL is required.");
                RequireField(context, metadata, "dest_blobTenantId", "Tenant ID is required.");
                RequireField(context, metadata, "dest_blobClientId", "Client ID is required.");
                break;
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

    /// <summary>Skips silently when the field is missing/blank — pair with <see cref="RequireField"/> for
    /// presence so a missing value doesn't also report as "wrong format".</summary>
    private static void RequirePattern(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata,
        string key,
        Regex pattern,
        string message)
    {
        if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) && !pattern.IsMatch(value))
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
