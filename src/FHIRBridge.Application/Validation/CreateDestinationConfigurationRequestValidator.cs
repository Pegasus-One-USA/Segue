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
        else if (request.DestinationType == DestinationType.Csv)
        {
            ValidateCsvMetadata(context, metadata);
        }
        else if (request.DestinationType is DestinationType.FhirRepository or DestinationType.AzureFhirService)
        {
            // Azure FHIR Service (Azure Health Data Services) is a standard FHIR R4 server, wire-compatible with
            // the generic FhirRepository destination — same dest_fhirAuthType-driven metadata shape, so it shares
            // this validation rather than duplicating it (see MappedFhirRepositoryDestinationWriter, which both
            // destination types are registered to).
            ValidateFhirRepositoryMetadata(request, context, metadata);
        }
        else if (request.DestinationType == DestinationType.BlobStorage)
        {
            ValidateBlobMetadata(context, metadata);
        }
        else if (request.DestinationType == DestinationType.DataLakeWebhook)
        {
            ValidateDataLakeWebhookMetadata(request, context, metadata);
        }
        else if (request.DestinationType is DestinationType.DataFabricAzure or DestinationType.DataFabricWarehouse)
        {
            // Both Fabric types share one metadata shape (workspace, item, Entra auth) and therefore one
            // validator. The landing mode is the only thing that differs, and for DataFabricWarehouse it is
            // implied by the type rather than read from dest_fabricMode — see ValidateDataFabricMetadata.
            ValidateDataFabricMetadata(request, context, metadata);
        }
        else if (request.DestinationType == DestinationType.ApiEndpoint)
        {
            ValidateApiEndpointMetadata(request, context, metadata);
        }
    }

    private static readonly string[] SupportedApiEndpointAuthModes =
        ["none", "bearer", "apiKeyHeader", "apiKeyQuery", "basic", "hmacSha256", "oauth2ClientCredentials", "clientCertificate"];

    private static readonly string[] SupportedApiEndpointHttpMethods = ["POST", "PUT", "PATCH", "DELETE"];

    private static readonly string[] SupportedApiEndpointPayloadShapes =
        ["jsonArray", "ndjson", "envelope", "recordPerRequest"];

    /// <summary>
    /// Server-side mirror of the API Endpoint wizard form. Endpoint URL is always required (unlike Data Lake
    /// Webhook, there is no "secret carries the URL" mode here), and https is only enforced when the caller opts
    /// into <c>dest_apiRequireHttps</c> — this destination is not assumed to always carry PHI the way Data Lake
    /// Webhook is, so plain http is allowed by default for an internal/test endpoint.
    /// </summary>
    private static void ValidateApiEndpointMetadata(
        CreateDestinationConfigurationRequest request,
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata)
    {
        var endpointUrl = FirstNonBlank(metadata.GetValueOrDefault("dest_apiEndpointUrl"), request.Target);
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            context.AddFailure("dest_apiEndpointUrl", "Endpoint URL is required.");
        }
        else if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out _))
        {
            // Checked BEFORE the https requirement below: IsHttpsOrLoopback also returns false for a value
            // that isn't a URI at all, so checking https first reported the wrong reason — "must use https"
            // for a value that fails at "isn't even a URL" — whenever Require HTTPS happened to be enabled.
            context.AddFailure("dest_apiEndpointUrl", "Endpoint URL must be an absolute URI.");
        }
        else
        {
            var requireHttps = string.Equals(
                metadata.GetValueOrDefault("dest_apiRequireHttps"), "true", StringComparison.OrdinalIgnoreCase);
            if (requireHttps && !IsHttpsOrLoopback(endpointUrl))
            {
                context.AddFailure(
                    "dest_apiEndpointUrl",
                    "Endpoint URL must use https — 'Require HTTPS' is enabled for this destination (loopback "
                        + "is permitted in development).");
            }
        }

        RequireOneOf(context, metadata, "dest_apiAuthMode", SupportedApiEndpointAuthModes, "auth mode");
        RequireOneOf(context, metadata, "dest_apiHttpMethod", SupportedApiEndpointHttpMethods, "HTTP method");
        RequireOneOf(context, metadata, "dest_apiPayloadShape", SupportedApiEndpointPayloadShapes, "payload shape");

        var authMode = metadata.GetValueOrDefault("dest_apiAuthMode", "none");

        if (string.Equals(authMode, "apiKeyHeader", StringComparison.OrdinalIgnoreCase))
        {
            RequireField(context, metadata, "dest_apiAuthHeaderName", "Header name is required for API key (header) auth.");
        }

        if (string.Equals(authMode, "oauth2ClientCredentials", StringComparison.OrdinalIgnoreCase))
        {
            RequireField(context, metadata, "dest_apiTokenEndpoint", "Token endpoint is required.");
            RequireField(context, metadata, "dest_apiClientId", "Client ID is required.");
        }

        RequireOptionalIntInRange(
            context, metadata, "dest_apiBatchSize", 1, 50000, "Batch size must be between 1 and 50000.");
        RequireOptionalIntInRange(
            context, metadata, "dest_apiMaxRequestBytes", 1024, 100663296,
            "Max request size must be between 1 KB and 96 MB.");
        RequireOptionalIntInRange(
            context, metadata, "dest_apiTimeoutSeconds", 1, 600, "Timeout must be between 1 and 600 seconds.");
        RequireOptionalIntInRange(
            context, metadata, "dest_apiRetryCount", 0, 10, "Retry count must be between 0 and 10.");

        if (metadata.TryGetValue("dest_apiBodyTemplateJson", out var bodyTemplate) && !string.IsNullOrWhiteSpace(bodyTemplate))
        {
            try
            {
                JsonDocument.Parse(bodyTemplate);
            }
            catch (JsonException)
            {
                context.AddFailure("dest_apiBodyTemplateJson", "Request Body Template must be valid JSON.");
            }
        }
    }

    private static readonly string[] SupportedDataLakeWebhookAuthModes =
        ["none", "bearer", "apiKeyHeader", "basic", "hmacSha256", "oauth2ClientCredentials"];

    private static readonly string[] SupportedDataLakeWebhookPayloadShapes =
        ["ndjson", "jsonArray", "envelope", "recordPerRequest"];

    /// <summary>
    /// Server-side mirror of the Data Lake Webhook wizard form, and the first place a misconfiguration can be
    /// reported inline instead of at first write. The https-only rule is the important one: unlike the Runtime
    /// plane's notifier node — which carries a write summary and refuses record-level data — this destination
    /// sends mapped record values, so plaintext delivery would put PHI on the wire in cleartext. Loopback is
    /// excepted so a developer can target a local collector. Mirrors
    /// <c>DataLakeWebhookSettings.ValidateEndpointUrl</c>, the writer-level last line of defense for the same rule
    /// (which also covers a URL arriving via the stored secret rather than through this request).
    /// </summary>
    private static void ValidateDataLakeWebhookMetadata(
        CreateDestinationConfigurationRequest request,
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata)
    {
        var authMode = metadata.GetValueOrDefault("dest_dlwAuthMode", "none");
        if (!SupportedDataLakeWebhookAuthModes.Contains(authMode, StringComparer.OrdinalIgnoreCase))
        {
            context.AddFailure(
                "dest_dlwAuthMode",
                $"Unsupported auth mode '{authMode}'. Supported values: "
                    + string.Join(", ", SupportedDataLakeWebhookAuthModes) + ".");
            return;
        }

        var payloadShape = metadata.GetValueOrDefault("dest_dlwPayloadShape", "ndjson");
        if (!SupportedDataLakeWebhookPayloadShapes.Contains(payloadShape, StringComparer.OrdinalIgnoreCase))
        {
            context.AddFailure(
                "dest_dlwPayloadShape",
                $"Unsupported payload shape '{payloadShape}'. Supported values: "
                    + string.Join(", ", SupportedDataLakeWebhookPayloadShapes) + ".");
        }

        var endpointUrl = FirstNonBlank(metadata.GetValueOrDefault("dest_dlwEndpointUrl"), request.Target);

        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            // Blank is legal for auth mode "none" only, where the stored secret is itself the pre-authorized
            // ingest URL (a Fabric Eventstream / Event Grid endpoint) — see DataLakeWebhookSettings.
            if (!string.Equals(authMode, "none", StringComparison.OrdinalIgnoreCase))
            {
                context.AddFailure(
                    "dest_dlwEndpointUrl",
                    "Endpoint URL is required unless auth mode is 'none', in which case the stored secret must be "
                        + "the full ingest URL.");
            }
        }
        else if (!IsHttpsOrLoopback(endpointUrl))
        {
            context.AddFailure(
                "dest_dlwEndpointUrl",
                "Endpoint URL must use https — a data lake webhook carries mapped record data (PHI), so plaintext "
                    + "delivery is not allowed (http is permitted only for loopback addresses in development).");
        }

        if (string.Equals(authMode, "apiKeyHeader", StringComparison.OrdinalIgnoreCase))
        {
            RequireField(context, metadata, "dest_dlwAuthHeaderName", "Header name is required for API key auth.");
        }

        if (string.Equals(authMode, "oauth2ClientCredentials", StringComparison.OrdinalIgnoreCase))
        {
            RequireField(context, metadata, "dest_dlwTokenEndpoint", "Token endpoint is required.");
            RequireField(context, metadata, "dest_dlwClientId", "Client ID is required.");
        }

        RequireOptionalIntInRange(
            context, metadata, "dest_dlwBatchSize", 1, 50000, "Batch size must be between 1 and 50000.");
        RequireOptionalIntInRange(
            context, metadata, "dest_dlwMaxRequestBytes", 1024, 100663296,
            "Max request size must be between 1 KB and 96 MB.");
        RequireOptionalIntInRange(
            context, metadata, "dest_dlwTimeoutSeconds", 1, 600, "Timeout must be between 1 and 600 seconds.");
        RequireOptionalIntInRange(
            context, metadata, "dest_dlwRetryCount", 0, 10, "Retry count must be between 0 and 10.");
    }

    private static readonly string[] SupportedFabricModes = ["oneLakeFiles", "warehouseTable", "eventstream"];
    // Only the item types a Fabric landing strategy can actually write to. KQLDatabase (Kusto ingestion, no
    // Files area) and MirroredDatabase (a read-only replica of an external source) used to be accepted here and
    // then failed mid-pipeline at run time; they are refused at save time instead. Mirrors
    // FabricDestinationSettings.SupportedItemTypes, which enforces the same list again at write time.
    private static readonly string[] SupportedFabricItemTypes = ["Lakehouse", "Warehouse"];
    private static readonly string[] SupportedFabricFileFormats = ["ndjson", "parquet", "csv"];
    private static readonly string[] SupportedFabricAuthModes = ["managedIdentity", "servicePrincipal"];
    private static readonly string[] SupportedFabricTableWriteModes = ["append", "upsert"];
    private static readonly string[] SupportedFabricPartitionSchemes =
        ["none", "resourceType", "ingestDate", "resourceTypeAndIngestDate"];

    /// <summary>
    /// Server-side mirror of the Microsoft Fabric wizard form. Two rules here exist to stop a configuration that
    /// would look accepted and then never produce what the customer expects: a landing mode other than OneLake
    /// Files is not implemented (Eventstream is served by the Data Lake Webhook destination instead), and a path
    /// under the Lakehouse <c>Tables/</c> area cannot work at all, because a Fabric table is a Delta table and this
    /// destination writes plain files. Mirrors <c>FabricDestinationSettings</c>, which enforces both again at
    /// write time for rows saved before this check existed.
    /// </summary>
    /// <summary>
    /// Server-side mirror of the Fabric WAREHOUSE wizard form. Shares the workspace/item/auth half with the
    /// OneLake Files form and replaces the file-landing half (file format, partitioning, base path — none of
    /// which mean anything for a table load) with the two things a COPY INTO cannot be performed without: the
    /// Warehouse's TDS endpoint, and the Lakehouse the staged Parquet lands in on the way. Mirrors
    /// <c>FabricDestinationSettings.Parse</c>, which requires both again at write time.
    /// </summary>
    private static void ValidateDataFabricWarehouseMetadata(
        CreateDestinationConfigurationRequest request,
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (string.IsNullOrWhiteSpace(
                FirstNonBlank(metadata.GetValueOrDefault("dest_fabricWorkspace"), request.Target)))
        {
            context.AddFailure("dest_fabricWorkspace", "Workspace is required.");
        }

        RequireField(context, metadata, "dest_fabricItemName", "Warehouse (item) name is required.");

        // A Warehouse load is the one Fabric surface that cannot target a Lakehouse: a Lakehouse's SQL analytics
        // endpoint is read-only, so COPY INTO against it fails (see FabricLandingMode.WarehouseTable). The item
        // type is pinned rather than offered as a choice.
        var itemType = metadata.GetValueOrDefault("dest_fabricItemType", "Warehouse");
        if (!string.Equals(itemType, "Warehouse", StringComparison.OrdinalIgnoreCase))
        {
            context.AddFailure(
                "dest_fabricItemType",
                $"A Fabric Warehouse destination must target a Warehouse item, not '{itemType}'. To land data in "
                    + "a Lakehouse, create a Microsoft Fabric (OneLake) destination instead.");
        }

        RequireField(
            context, metadata, "dest_fabricWarehouseSqlEndpoint",
            "Warehouse SQL (TDS) endpoint is required.");
        RequireField(
            context, metadata, "dest_fabricWarehouseStagingLakehouse",
            "A staging Lakehouse is required — a Warehouse has no Files area of its own, so COPY INTO reads the "
                + "staged Parquet from a Lakehouse in the same workspace.");

        RequireOneOf(context, metadata, "dest_fabricAuthMode", SupportedFabricAuthModes, "auth mode");
    }

    private static void ValidateDataFabricMetadata(
        CreateDestinationConfigurationRequest request,
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata)
    {
        // DataFabricWarehouse IS the Warehouse surface, so its mode comes from the type and dest_fabricMode is
        // not consulted (FabricDestinationSettings.Parse derives it the same way). The Warehouse branch returns
        // below, before any of the OneLake-Files-specific file-format/path rules.
        if (request.DestinationType == DestinationType.DataFabricWarehouse)
        {
            ValidateDataFabricWarehouseMetadata(request, context, metadata);
            return;
        }

        var mode = metadata.GetValueOrDefault("dest_fabricMode", "oneLakeFiles");
        if (!SupportedFabricModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            context.AddFailure(
                "dest_fabricMode",
                $"Unsupported Fabric landing mode '{mode}'. Supported values: "
                    + string.Join(", ", SupportedFabricModes) + ".");
            return;
        }

        if (string.Equals(mode, "eventstream", StringComparison.OrdinalIgnoreCase))
        {
            context.AddFailure(
                "dest_fabricMode",
                "Fabric Eventstream is not available as a Fabric landing mode. An Eventstream custom endpoint is "
                    + "authenticated HTTP — create a Data Lake Webhook destination with that endpoint URL instead.");
            return;
        }


        if (string.IsNullOrWhiteSpace(
                FirstNonBlank(metadata.GetValueOrDefault("dest_fabricWorkspace"), request.Target)))
        {
            context.AddFailure("dest_fabricWorkspace", "Workspace is required.");
        }

        RequireField(context, metadata, "dest_fabricItemName", "Lakehouse (item) name is required.");
        RequireOneOf(context, metadata, "dest_fabricItemType", SupportedFabricItemTypes, "item type");
        RequireOneOf(context, metadata, "dest_fabricFileFormat", SupportedFabricFileFormats, "file format");
        RequireOneOf(context, metadata, "dest_fabricPartitionBy", SupportedFabricPartitionSchemes, "partition scheme");
        RequireOneOf(context, metadata, "dest_fabricAuthMode", SupportedFabricAuthModes, "authentication mode");

        if (string.Equals(
                metadata.GetValueOrDefault("dest_fabricAuthMode", "managedIdentity"),
                "servicePrincipal",
                StringComparison.OrdinalIgnoreCase))
        {
            RequireField(context, metadata, "dest_fabricTenantId", "Tenant ID is required.");
            RequireField(context, metadata, "dest_fabricClientId", "Client ID is required.");
        }

        // Warehouse mode needs a TDS endpoint (a different service from OneLake, so not derivable) and the
        // Lakehouse a COPY INTO stages through (a Warehouse has no Files area). Mirrors the same two checks in
        // FabricDestinationSettings.Parse so the failure lands at save time, not mid-load.
        if (string.Equals(mode, "warehouseTable", StringComparison.OrdinalIgnoreCase))
        {
            RequireField(
                context,
                metadata,
                "dest_fabricWarehouseSqlEndpoint",
                "Warehouse SQL connection string is required.");
            RequireField(
                context,
                metadata,
                "dest_fabricWarehouseStagingLakehouse",
                "Staging lakehouse is required — a Warehouse has no Files area, so COPY INTO stages through a "
                    + "Lakehouse in the same workspace.");
            RequireOneOf(
                context, metadata, "dest_fabricWarehouseWriteMode", SupportedFabricTableWriteModes, "write mode");

            if (string.Equals(
                    metadata.GetValueOrDefault("dest_fabricItemType"),
                    "Lakehouse",
                    StringComparison.OrdinalIgnoreCase))
            {
                context.AddFailure(
                    "dest_fabricItemType",
                    "Warehouse landing mode needs item type Warehouse. A Lakehouse's SQL analytics endpoint looks "
                        + "similar but is read-only, so COPY INTO against it fails.");
            }
        }

        ValidateFabricPath(context, metadata);
    }

    private static void ValidateFabricPath(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("dest_fabricPath", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            // Blank means the writer's own default ("fhirbridge") applies — nothing to check.
            return;
        }

        var path = raw.Trim().Replace('\\', '/').Trim('/');

        if (path.Contains("://", StringComparison.Ordinal))
        {
            context.AddFailure("dest_fabricPath", "Path must be relative to the item's Files area, not a full URL.");
            return;
        }

        if (path.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["Files/".Length..].Trim('/');
        }

        if (path.Equals("Tables", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("Tables/", StringComparison.OrdinalIgnoreCase))
        {
            context.AddFailure(
                "dest_fabricPath",
                "The Lakehouse 'Tables/' area holds Delta tables, and this destination writes plain files (no Delta "
                    + "transaction log), so files landed there would never register as a table. Use the Files area "
                    + "and surface it as a table with a Fabric shortcut, notebook or pipeline.");
            return;
        }

        if (path.Contains(".Lakehouse", StringComparison.OrdinalIgnoreCase)
            || path.Contains(".Warehouse", StringComparison.OrdinalIgnoreCase))
        {
            context.AddFailure(
                "dest_fabricPath",
                "Path must not repeat the item name — the item is already addressed by the Lakehouse name and type.");
            return;
        }

        if (path.Split('/').Any(segment => segment is "." or ".."))
        {
            context.AddFailure("dest_fabricPath", "Path must not contain '.' or '..' segments.");
        }
    }

    private static bool IsHttpsOrLoopback(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    /// <summary>Blank is valid — it means "use the writer's default"; only a supplied value is checked.</summary>
    private static void RequireOneOf(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string[] allowed,
        string fieldLabel)
    {
        if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            context.AddFailure(
                key, $"Unsupported {fieldLabel} '{value}'. Supported values: " + string.Join(", ", allowed) + ".");
        }
    }

    /// <summary>Like <see cref="RequireIntInRange"/> but a missing/blank value is accepted (the writer's default
    /// applies) — only a supplied one must parse and be in range.</summary>
    private static void RequireOptionalIntInRange(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata,
        string key,
        int min,
        int max,
        string message)
    {
        if (!metadata.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (!int.TryParse(raw, out var value) || value < min || value > max)
        {
            context.AddFailure(key, message);
        }
    }

    private static readonly string[] SupportedFhirAuthTypes = ["none", "bearer", "basic", "clientCredentials", "managedIdentity"];

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
                $"Unsupported FHIR auth type '{authType}'. Supported values: none, bearer, basic, clientCredentials, managedIdentity.");
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

    /// <summary>
    /// Azure Blob container naming rules: 3-63 characters, lowercase letters/digits/hyphens only, must start and
    /// end with a letter or digit, no consecutive hyphens. A name violating this is accepted by this API but
    /// rejected by Azure itself with an opaque "InvalidResourceName" error at write time — catching it here
    /// gives the wizard an inline, actionable error instead.
    /// </summary>
    private static readonly Regex BlobContainerNameRegex = new(
        @"^(?!.*--)[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.Compiled);

    // Azure blob names: no backslash (not a supported path delimiter — "/" is) and no control characters;
    // checked separately below is that the pattern must not end with "." or "/". Mirrors
    // BlobDestinationSettings.ValidatePattern, the writer-level last line of defense for the same rule.
    private static readonly Regex BlobPatternDisallowedCharacters = new(@"[\\\x00-\x1F\x7F]", RegexOptions.Compiled);
    private const int MaxBlobPatternLength = 512;

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

        RequireValidBlobNamingPattern(context, metadata, "dest_blobFolderPattern", "Folder pattern", allowNestedFolders: true);
        RequireValidBlobNamingPattern(context, metadata, "dest_blobFileNamePattern", "File name pattern", allowNestedFolders: false);
    }

    /// <summary>Blank is always valid here — it means "use the selected Record mode's own default" (see
    /// BlobDestinationSettings.ParseFolderPattern/ParseFileNamePattern) — only a non-blank override is checked
    /// against what Azure itself allows in a blob name. File name pattern (allowNestedFolders: false) additionally
    /// forbids "/" anywhere — nesting belongs in Folder pattern; a "/" here would split the record's blob into
    /// extra folders instead of naming a single file.</summary>
    private static void RequireValidBlobNamingPattern(
        ValidationContext<CreateDestinationConfigurationRequest> context,
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string fieldLabel,
        bool allowNestedFolders)
    {
        if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > MaxBlobPatternLength)
        {
            context.AddFailure(key, $"{fieldLabel} is too long — Azure blob names cannot exceed 1024 characters.");
            return;
        }

        if (BlobPatternDisallowedCharacters.IsMatch(value))
        {
            context.AddFailure(
                key,
                $"{fieldLabel} cannot contain a backslash or control characters — use \"/\" for nested folders instead of \"\\\".");
            return;
        }

        if (!allowNestedFolders && value.Contains('/'))
        {
            context.AddFailure(
                key,
                $"{fieldLabel} cannot contain \"/\" — that would split it into extra folders instead of naming a single file. Use Folder pattern for nesting instead.");
            return;
        }

        if (value.EndsWith('.') || value.EndsWith('/'))
        {
            context.AddFailure(key, $"{fieldLabel} cannot end with \".\" or \"/\" — Azure rejects blob names ending that way.");
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
