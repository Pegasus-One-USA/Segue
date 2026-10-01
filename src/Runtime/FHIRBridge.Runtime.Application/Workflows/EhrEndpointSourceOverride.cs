using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// The "run this workflow against a specific hospital" path. Entirely separate from the default run: it is only
/// called when the run's context carries a resolved <see cref="EhrEndpointOverride"/>, and it works on a copy of the
/// already-resolved source configuration, so a run without an EHR Endpoint never reaches this code and the source
/// connection record is never modified.
/// </summary>
public static class EhrEndpointSourceOverride
{
    public static FhirSourceConfiguration Apply(FhirSourceConfiguration source, EhrEndpointOverride endpoint)
    {
        // Interactive sources (EHR launch / standalone / patient) get their hospital from the sign-in itself, so they
        // are left exactly as they are.
        if (source.ApplicationType is { } applicationType && applicationType != ApplicationType.Backend)
        {
            return source;
        }

        if (!string.Equals(source.SourceType.ToString(), endpoint.Vendor, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"EHR Endpoint '{endpoint.Name}' is a {endpoint.Vendor} endpoint, but this workflow's source is {source.SourceType}.");
        }

        // A bulk export is polled later from the saved connection (BulkExportPollService), which would silently go to
        // the default hospital. Refuse rather than mix two hospitals in one export.
        if (string.Equals(source.RetrievalMethod, "bulk-export", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Bulk export cannot be run against a specific EHR Endpoint yet; run it with the workflow's default configuration.");
        }

        return source with
        {
            BaseUrl = endpoint.BaseUrl,
            TokenEndpoint = Pick(endpoint.TokenEndpoint, source.TokenEndpoint),
            ClientId = Pick(endpoint.ClientId, source.ClientId),
            KeyId = Pick(endpoint.KeyId, source.KeyId),
            PracticeId = Pick(endpoint.PracticeId, source.PracticeId),
            EhrEndpointId = endpoint.Id,
            LastUpdatedWatermarks = null,
            Since = null,
            RunOverridesActive = true,
        };
    }

    private static string? Pick(string? overrideValue, string? fallback) =>
        string.IsNullOrWhiteSpace(overrideValue) ? fallback : overrideValue;
}
