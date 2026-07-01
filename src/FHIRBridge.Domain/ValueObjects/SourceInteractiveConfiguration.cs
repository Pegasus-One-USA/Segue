using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Domain.ValueObjects;

/// <summary>
/// Configuration specific to an interactive (authorization-code) source connection — the parts that are not
/// discoverable from the server's SMART metadata: the registered redirect URI(s), the standalone launch URL, the
/// patient-selection method, and, for EHR launch, the trusted-issuer allow-list validated before redirect (anti
/// token-phishing). Null on non-interactive (Backend) sources.
/// </summary>
public sealed class SourceInteractiveConfiguration
{
    private SourceInteractiveConfiguration()
    {
    }

    public SourceInteractiveConfiguration(
        string[] redirectUris,
        string? launchUrl,
        string[] trustedIssuers,
        PatientSelectionMethod? patientSelectionMethod = null)
    {
        RedirectUris = redirectUris ?? [];
        LaunchUrl = launchUrl;
        TrustedIssuers = trustedIssuers ?? [];
        PatientSelectionMethod = patientSelectionMethod;
    }

    public string[] RedirectUris { get; private set; } = [];
    public string? LaunchUrl { get; private set; }
    public string[] TrustedIssuers { get; private set; } = [];

    /// <summary>How the patient context is established (standalone); null when not applicable.</summary>
    public PatientSelectionMethod? PatientSelectionMethod { get; private set; }
}
