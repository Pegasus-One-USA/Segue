using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Manifests;

/// <summary>
/// Declarative, human-authored description of a tenant's full configuration. Deserialized from a YAML manifest and
/// materialized by <see cref="Services.IYamlManifestImportService"/> through the existing tenant configuration APIs.
/// Sources, destinations, webhooks and mapping profiles are referenced by <c>name</c> inside the manifest; the import
/// service resolves those names to the identifiers assigned when each entity is created.
/// </summary>
public sealed class TenantManifest
{
    public TenantManifestHeader Tenant { get; set; } = new();

    public List<SourceManifest> Sources { get; set; } = [];

    public List<DestinationManifest> Destinations { get; set; } = [];

    public List<WebhookManifest> Webhooks { get; set; } = [];

    public List<MappingProfileManifest> Mappings { get; set; } = [];

    public List<ResourceManifest> Resources { get; set; } = [];
}

public sealed class TenantManifestHeader
{
    public string Name { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;
}

public sealed class SourceManifest
{
    public string Name { get; set; } = string.Empty;

    public SourceSystemType SystemType { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public SourceAuthenticationManifest Authentication { get; set; } = new();
}

public sealed class SourceAuthenticationManifest
{
    public AuthenticationType Type { get; set; }

    public string? ClientId { get; set; }

    public string? TokenEndpoint { get; set; }

    public List<string> Scopes { get; set; } = [];

    public string? ClientSecretKeyVaultName { get; set; }

    public string? ClientSecretName { get; set; }

    public string? PrivateKeyKeyVaultName { get; set; }

    public string? PrivateKeySecretName { get; set; }

    public string? KeyId { get; set; }
}

public sealed class DestinationManifest
{
    public string Name { get; set; } = string.Empty;

    public DestinationType Type { get; set; }

    public string KeyVaultName { get; set; } = string.Empty;

    public string SecretName { get; set; } = string.Empty;

    public string? Target { get; set; }
}

public sealed class WebhookManifest
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the source connection (declared under <c>sources</c>) this webhook belongs to.</summary>
    public string Source { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
}

public sealed class MappingProfileManifest
{
    public string Name { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    /// <summary>Name of the source connection (declared under <c>sources</c>) this profile ingests from.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Name of the destination (declared under <c>destinations</c>) this profile writes to.</summary>
    public string Destination { get; set; } = string.Empty;

    public string DestinationObject { get; set; } = string.Empty;

    public List<MappingFieldManifest> Fields { get; set; } = [];
}

public sealed class MappingFieldManifest
{
    public string TargetField { get; set; } = string.Empty;

    public string JsonPath { get; set; } = string.Empty;

    public MappingValueType ValueType { get; set; }

    public bool IsRequired { get; set; }

    public string? DefaultValue { get; set; }

    public string? Format { get; set; }

    public string? NormalizationType { get; set; }

    public string? TerminologySystemJsonPath { get; set; }

    public string? TerminologyCodeJsonPath { get; set; }
}

public sealed class ResourceManifest
{
    public string ResourceType { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public IngestionMode IngestionMode { get; set; }

    /// <summary>Optional webhook name; required when the ingestion mode includes webhook delivery.</summary>
    public string? Webhook { get; set; }

    /// <summary>
    /// Name of the mapping profile for this resource's primary route. The mapping owns the resource type, source,
    /// and destination — those are not declared on the resource entry.
    /// </summary>
    public string Mapping { get; set; } = string.Empty;

    public string? ScheduleExpression { get; set; }

    public string? SearchParameters { get; set; }

    /// <summary>Optional additional routes layered on top of the primary route.</summary>
    public List<ResourceRouteManifest> AdditionalRoutes { get; set; } = [];
}

public sealed class ResourceRouteManifest
{
    public IngestionMode IngestionMode { get; set; }

    public string? Webhook { get; set; }

    /// <summary>Name of the mapping profile for this route. The mapping owns the source, destination, and resource type.</summary>
    public string Mapping { get; set; } = string.Empty;

    public string? ScheduleExpression { get; set; }

    public string? SearchParameters { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int Priority { get; set; }
}
