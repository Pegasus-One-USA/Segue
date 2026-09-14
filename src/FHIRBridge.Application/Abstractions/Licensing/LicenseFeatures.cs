namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>
/// String constants for the feature flags a signed license may grant (the <c>features</c> claim minted
/// by the license tool — see <see cref="ILicenseService"/> / <c>SignedLicenseValidator</c>). Deliberately a
/// small, illustrative set rather than an exhaustive catalog of every capability in the product — add a
/// constant here as each feature actually becomes license-gated in a later stage. This stage
/// (verification/reporting only) never checks these against anything; a second implementation stage is
/// expected to call <see cref="ILicenseService.HasFeature"/> with values drawn from this class.
///
/// Destination-type gating used to be stand-in'd here via ad-hoc <c>"dest:*"</c> entries (e.g.
/// <c>"dest:mssql"</c>, <c>"dest:sftp"</c>) before <c>LicenseLimits.AllowedDestinationTypes</c> existed.
/// That convention is retired now that a proper field exists — see <c>LicenseLimits.AllowedDestinationTypes</c>
/// (and <c>AllowedResourceTypes</c>/<c>AllowedSourceTypes</c> for the same split on the other two axes).
/// <c>Features</c> is pure capability toggles only from here on.
/// </summary>
public static class LicenseFeatures
{
    /// <summary>HL7 v2 MLLP ingestion (<c>Hl7MllpListenerService</c>).</summary>
    public const string Hl7Mllp = "hl7-mllp";

    /// <summary>HIPAA Safe Harbor de-identification (<c>SafeHarborDeIdentificationService</c>).</summary>
    public const string DeIdentification = "deid";

    /// <summary>The explicit-DAG Runtime workflow plane (<c>PipelineOrchestrator</c>, <c>/api/v1/workflows</c>).</summary>
    public const string RuntimePlane = "runtime-plane";
}
