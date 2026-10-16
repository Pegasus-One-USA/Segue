using FHIRBridge.SharedKernel.Abstractions;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A payer (insurer / plan) the Coverage Requirements Discovery (Da Vinci CRD) service answers for. A CRD call is
/// matched to a payer through the patient's active Coverage: <see cref="Identifiers"/> are compared with
/// <c>Coverage.payor</c> (identifier, display, or the referenced Organization's identifier / name) and, when
/// <see cref="PlanValues"/> is not empty, one of them must also equal a <c>Coverage.class.value</c>.
/// <para>
/// Segue answers from its own <see cref="CdsCoverageRule"/> rows, or, when <see cref="RelayEnabled"/>, forwards the
/// call to the payer's own CRD service (<see cref="RelayServiceUrl"/>) as an intermediary, signing with Segue's key.
/// Nothing here is secret: the relay is authenticated by Segue's own signing key (published at the CDS JWKS URL).
/// </para>
/// </summary>
public sealed class CdsPayer : AuditableEntity<Guid>, IHasAuditDisplayName
{
    public const int NameMaxLength = 200;
    public const int IdentifierMaxLength = 200;
    public const int MaxIdentifiers = 20;
    public const int MaxPlanValues = 50;
    public const int UrlMaxLength = 1000;
    public const int ClientIdMaxLength = 200;
    public const int MinRelayTimeoutMs = 100;
    public const int MaxRelayTimeoutMs = 10_000;
    public const int DefaultRelayTimeoutMs = 1000;

    private CdsPayer()
    {
    }

    public CdsPayer(
        string name,
        IEnumerable<string>? identifiers,
        IEnumerable<string>? planValues,
        bool isEnabled,
        bool relayEnabled = false,
        string? relayServiceUrl = null,
        int relayTimeoutMs = DefaultRelayTimeoutMs,
        bool fallbackToLocalRules = true,
        bool forwardFhirAuthorization = false,
        string? relayClientId = null)
    {
        Id = Guid.NewGuid();
        Update(name, identifiers, planValues, isEnabled, relayEnabled, relayServiceUrl, relayTimeoutMs, fallbackToLocalRules,
            forwardFhirAuthorization, relayClientId);
    }

    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;

    /// <summary>
    /// Values matched against <c>Coverage.payor</c>: <c>value</c> or <c>system|value</c> for an identifier, or a
    /// name compared (case-insensitively) with the payor's display or Organization name. At least one.
    /// </summary>
    public List<string> Identifiers { get; private set; } = [];

    /// <summary>Optional <c>Coverage.class.value</c>s (plan, group, ...); when not empty one must be present.</summary>
    public List<string> PlanValues { get; private set; } = [];

    public bool IsEnabled { get; private set; }

    /// <summary>Forward CRD calls for this payer to its own CRD service instead of answering from local rules.</summary>
    public bool RelayEnabled { get; private set; }

    /// <summary>The payer's CDS service endpoint (absolute https), e.g. https://crd.payer.example/cds-services/order-sign-crd.</summary>
    public string? RelayServiceUrl { get; private set; }

    /// <summary>How long Segue waits for the payer (100 - 10 000 ms; always cut to what is left of the call's budget).</summary>
    public int RelayTimeoutMs { get; private set; } = DefaultRelayTimeoutMs;

    /// <summary>When the relay fails or times out, answer from local rules instead (default on).</summary>
    public bool FallbackToLocalRules { get; private set; } = true;

    /// <summary>
    /// Pass the EHR's <c>fhirServer</c> and <c>fhirAuthorization</c> (its bearer token) on to the payer. Off by
    /// default: the EHR's token is never forwarded unless an admin explicitly allows it for this payer.
    /// </summary>
    public bool ForwardFhirAuthorization { get; private set; }

    /// <summary>The <c>sub</c> Segue puts in the JWT it signs for this payer (the client id the payer registered for Segue).</summary>
    public string? RelayClientId { get; private set; }

    public void Update(
        string name,
        IEnumerable<string>? identifiers,
        IEnumerable<string>? planValues,
        bool isEnabled,
        bool relayEnabled = false,
        string? relayServiceUrl = null,
        int relayTimeoutMs = DefaultRelayTimeoutMs,
        bool fallbackToLocalRules = true,
        bool forwardFhirAuthorization = false,
        string? relayClientId = null)
    {
        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            throw new BusinessRuleException("Payer name is required.");
        }

        if (trimmedName.Length > NameMaxLength)
        {
            throw new BusinessRuleException($"Payer name must be {NameMaxLength} characters or fewer.");
        }

        var ids = CleanList(identifiers, IdentifierMaxLength, MaxIdentifiers, "payer identifier");
        if (ids.Count == 0)
        {
            throw new BusinessRuleException(
                "Enter at least one payer identifier (the Coverage.payor identifier value, system|value, or the payer's name as the EHR shows it).");
        }

        var plans = CleanList(planValues, IdentifierMaxLength, MaxPlanValues, "plan value");

        var url = relayServiceUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            url = null;
        }
        else
        {
            if (url.Length > UrlMaxLength)
            {
                throw new BusinessRuleException($"Relay service URL must be {UrlMaxLength} characters or fewer.");
            }

            if (!IsHttpsUrl(url))
            {
                throw new BusinessRuleException("Relay service URL must be an absolute https URL with no query string.");
            }
        }

        if (relayEnabled && url is null)
        {
            throw new BusinessRuleException("Enter the payer's CRD service URL before switching the relay on.");
        }

        if (relayTimeoutMs is < MinRelayTimeoutMs or > MaxRelayTimeoutMs)
        {
            throw new BusinessRuleException($"Relay timeout must be between {MinRelayTimeoutMs} and {MaxRelayTimeoutMs} ms.");
        }

        var clientId = relayClientId?.Trim();
        if (clientId is { Length: > ClientIdMaxLength })
        {
            throw new BusinessRuleException($"Relay client id must be {ClientIdMaxLength} characters or fewer.");
        }

        Name = trimmedName;
        Identifiers = ids;
        PlanValues = plans;
        IsEnabled = isEnabled;
        RelayEnabled = relayEnabled;
        RelayServiceUrl = url;
        RelayTimeoutMs = relayTimeoutMs;
        FallbackToLocalRules = fallbackToLocalRules;
        ForwardFhirAuthorization = forwardFhirAuthorization;
        RelayClientId = string.IsNullOrEmpty(clientId) ? null : clientId;
    }

    /// <summary>True when calls for this payer go to its own CRD service.</summary>
    public bool UsesRelay => RelayEnabled && RelayServiceUrl is not null;

    /// <summary>An absolute https URL with no query, fragment or user info.</summary>
    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.UserInfo);

    private static List<string> CleanList(IEnumerable<string>? values, int maxLength, int maxCount, string label)
    {
        var cleaned = new List<string>();
        foreach (var value in values ?? [])
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed) || cleaned.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Length > maxLength)
            {
                throw new BusinessRuleException($"Each {label} must be {maxLength} characters or fewer.");
            }

            cleaned.Add(trimmed);
        }

        if (cleaned.Count > maxCount)
        {
            throw new BusinessRuleException($"At most {maxCount} {label}s.");
        }

        return cleaned;
    }
}
