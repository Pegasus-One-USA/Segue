using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// athenahealth FHIR R4 source connector. Reuses <see cref="FhirSourceConnectorBase"/>'s paginated search/retry/
/// throttle machinery; the only real differences from Epic are request-level practice scoping (<c>ah-practice</c>,
/// required on every call — a request without it fails with a 400 <c>business-rule</c> "Could not determine what
/// practice") and one per-resource-type default search parameter (<c>MedicationRequest</c> requires <c>intent</c>).
/// </summary>
public sealed class AthenahealthFhirSourceClient : FhirSourceConnectorBase
{
    public AthenahealthFhirSourceClient(
        HttpClient httpClient,
        IFhirAccessTokenProvider accessTokenProvider,
        IOptions<EpicFhirClientOptions>? options = null,
        ILogger<AthenahealthFhirSourceClient>? logger = null)
        : base(httpClient, accessTokenProvider, options?.Value, logger)
    {
    }

    protected override string SourceDisplayName => "athenahealth FHIR";

    protected override IReadOnlyDictionary<string, string> AdditionalQueryParameters(FhirSourceConfiguration source)
    {
        if (string.IsNullOrWhiteSpace(source.PracticeId))
        {
            return base.AdditionalQueryParameters(source);
        }

        return new Dictionary<string, string> { ["ah-practice"] = BuildPracticeReference(source.PracticeId) };
    }

    /// <summary>
    /// athenahealth expects <c>ah-practice</c> as a FHIR reference, not a bare number — accept either from the
    /// stored PracticeId (the wizard only ever collects the bare number, e.g. "195900") and construct the
    /// reference form the API actually requires.
    /// </summary>
    private static string BuildPracticeReference(string practiceId) =>
        practiceId.All(char.IsDigit) ? $"Organization/a-1.Practice-{practiceId}" : practiceId;

    /// <summary>
    /// athenahealth requires <c>MedicationRequest</c> searches to carry <c>intent</c> (order = prescriptions) —
    /// distinct from Epic's status-based default. Every other resource type falls back to no default, since
    /// athenahealth's category vocabulary for Observation/Condition hasn't been confirmed against a live sandbox
    /// (tracked as a follow-up; add entries here if a live search comes back 400 for a missing category/code).
    /// </summary>
    protected override IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> DefaultSearchParametersByResourceType { get; } =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["MedicationRequest"] = ("intent", "order"),
        };
}
