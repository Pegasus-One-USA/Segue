using System.Text.Json;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Connection details for an Azure Health Data Services FHIR service destination, parsed from
/// <see cref="DestinationConfiguration.ConnectionMetadataJson"/>. The client secret (client-credentials mode only)
/// is deliberately not carried here — it lives only in <see cref="DestinationConfiguration.SecretReference"/>'s
/// Key Vault entry, resolved separately by the writer.
/// </summary>
public sealed record AzureHealthDataServicesConnectionOptions(
    string FhirServiceUrl,
    string AuthMode,
    string? TenantId,
    string? ClientId,
    string? Scope,
    string? ManagedIdentityClientId)
{
    public const string ClientCredentialsMode = "clientCredentials";
    public const string ManagedIdentityMode = "managedIdentity";

    public bool IsManagedIdentity => string.Equals(AuthMode, ManagedIdentityMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The AAD scope to request a token for — the FHIR service's own audience unless overridden.</summary>
    public string ResolveScope() =>
        string.IsNullOrWhiteSpace(Scope) ? $"{FhirServiceUrl.TrimEnd('/')}/.default" : Scope;

    public static AzureHealthDataServicesConnectionOptions Parse(DestinationConfiguration destination)
    {
        var metadata = ParseMetadata(destination.ConnectionMetadataJson);

        var fhirServiceUrl = destination.Target;
        if (string.IsNullOrWhiteSpace(fhirServiceUrl))
        {
            metadata.TryGetValue("dest_fhirServiceUrl", out fhirServiceUrl);
        }

        if (string.IsNullOrWhiteSpace(fhirServiceUrl))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' is missing its Azure Health Data Services FHIR service URL.");
        }

        var authMode = metadata.GetValueOrDefault("dest_authMode", ClientCredentialsMode);

        return new AzureHealthDataServicesConnectionOptions(
            fhirServiceUrl,
            authMode,
            metadata.GetValueOrDefault("dest_tenantId"),
            metadata.GetValueOrDefault("dest_clientId"),
            metadata.GetValueOrDefault("dest_scope"),
            metadata.GetValueOrDefault("dest_managedIdentityClientId"));
    }

    private static Dictionary<string, string> ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

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
