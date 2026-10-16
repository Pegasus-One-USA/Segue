using System.Text.Json;
using FHIRBridge.SharedKernel.Abstractions;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// An EHR (or other CDS Hooks client) allowed to call Segue's CDS services. A call is accepted only when its
/// JWT's <c>iss</c> equals <see cref="Issuer"/> of an enabled row and its signature verifies against that row's
/// keys: fetched from <see cref="JwksUrl"/>, or the key set pasted into <see cref="JwksJson"/> for a client that
/// publishes no JWKS endpoint. Public keys only, so nothing here is a secret.
/// </summary>
public sealed class CdsTrustedIssuer : AuditableEntity<Guid>, IHasAuditDisplayName
{
    public const int NameMaxLength = 200;
    public const int VendorMaxLength = 100;
    public const int IssuerMaxLength = 500;
    public const int UrlMaxLength = 1000;
    public const int SubjectMaxLength = 500;

    private CdsTrustedIssuer()
    {
    }

    public CdsTrustedIssuer(
        string name,
        string vendor,
        string issuer,
        string? jwksUrl,
        string? jwksJson,
        string? audience,
        bool isEnabled,
        string? expectedSubject = null,
        string? fhirBaseUrl = null,
        bool allowCallback = true)
    {
        Id = Guid.NewGuid();
        Update(name, vendor, issuer, jwksUrl, jwksJson, audience, isEnabled, expectedSubject, fhirBaseUrl, allowCallback);
    }

    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;

    /// <summary>Free text naming the calling EHR (e.g. "Epic", "athenahealth", "Other"). Display only.</summary>
    public string Vendor { get; private set; } = default!;

    /// <summary>The exact <c>iss</c> claim the client puts in its JWT.</summary>
    public string Issuer { get; private set; } = default!;

    public string? JwksUrl { get; private set; }

    /// <summary>A pasted JSON Web Key Set (<c>{"keys":[...]}</c>), used when <see cref="JwksUrl"/> is empty.</summary>
    public string? JwksJson { get; private set; }

    /// <summary>
    /// The <c>aud</c> value to accept instead of the called service's own URL, for a client that signs with a
    /// fixed audience. Null means the audience must be the exact service URL.
    /// </summary>
    public string? Audience { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>
    /// The <c>sub</c> claim the client must send (Epic: the CDS client id of the health system's Epic build).
    /// Null means <c>sub</c> is not checked.
    /// </summary>
    public string? ExpectedSubject { get; private set; }

    /// <summary>
    /// The FHIR base URL this EHR's <c>fhirServer</c> must equal (normalized) before Segue calls back to it for
    /// missing prefetch data. Null means the issuer's <see cref="Issuer"/> value is used (Epic's <c>iss</c> is its
    /// FHIR base, e.g. https://host/api/FHIR/R4). http(s), no query or fragment; the callback client calls an http
    /// base only when Cds:AllowInsecureCallback is on.
    /// </summary>
    public string? FhirBaseUrl { get; private set; }

    /// <summary>
    /// Whether Segue may call this EHR's <c>fhirServer</c> (with the request's own <c>fhirAuthorization</c>
    /// token) to fetch missing prefetch data. On by default.
    /// </summary>
    public bool AllowCallback { get; private set; } = true;

    /// <summary>The FHIR base a callback must target: <see cref="FhirBaseUrl"/>, else <see cref="Issuer"/>.</summary>
    public string EffectiveFhirBaseUrl => FhirBaseUrl ?? Issuer;

    public void Update(
        string name,
        string vendor,
        string issuer,
        string? jwksUrl,
        string? jwksJson,
        string? audience,
        bool isEnabled,
        string? expectedSubject = null,
        string? fhirBaseUrl = null,
        bool allowCallback = true)
    {
        Name = Required(name, NameMaxLength, "Name");
        Vendor = Required(vendor, VendorMaxLength, "Vendor");
        Issuer = Required(issuer, IssuerMaxLength, "Issuer");
        JwksUrl = OptionalHttpsUrl(jwksUrl, "JWKS URL");
        JwksJson = OptionalJwks(jwksJson);
        Audience = Optional(audience, IssuerMaxLength, "Audience");
        IsEnabled = isEnabled;
        ExpectedSubject = Optional(expectedSubject, SubjectMaxLength, "Expected subject (sub)");
        FhirBaseUrl = OptionalFhirBaseUrl(fhirBaseUrl);
        AllowCallback = allowCallback;

        if (JwksUrl is null && JwksJson is null)
        {
            throw new BusinessRuleException("Enter a JWKS URL or paste the issuer's JSON Web Key Set.");
        }
    }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;

    /// <summary>
    /// True when a token header's <c>jku</c> names this issuer's configured <see cref="JwksUrl"/> (compared with
    /// <see cref="NormalizeUrl"/>). Keys are never fetched from a jku that is not configured here.
    /// </summary>
    public bool IsTrustedJku(string? jku) =>
        JwksUrl is not null && NormalizeUrl(jku) is { } normalized && normalized == NormalizeUrl(JwksUrl);

    /// <summary>True when a request's <c>fhirServer</c> is this issuer's <see cref="EffectiveFhirBaseUrl"/> (normalized).</summary>
    public bool IsTrustedFhirServer(string? fhirServer) =>
        NormalizeUrl(fhirServer) is { } normalized && normalized == NormalizeUrl(EffectiveFhirBaseUrl);

    /// <summary>
    /// A URL in comparable form: absolute http(s), scheme and host lower-cased, default port dropped, path kept
    /// case-sensitive without a trailing slash, query and fragment kept as given. Null when not an absolute http(s) URL.
    /// </summary>
    public static string? NormalizeUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{port}{path}{uri.Query}";
    }

    private static string Required(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new BusinessRuleException($"{label} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new BusinessRuleException($"{label} must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new BusinessRuleException($"{label} must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }

    /// <summary>
    /// True for an absolute https URL. Signing keys fetched over plain http could be swapped in transit, letting
    /// anyone on the path sign tokens Segue would accept, so a JWKS URL must be https.
    /// </summary>
    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string? OptionalHttpsUrl(string? value, string label)
    {
        var trimmed = Optional(value, UrlMaxLength, label);
        if (trimmed is null)
        {
            return null;
        }

        if (!IsHttpsUrl(trimmed))
        {
            throw new BusinessRuleException($"{label} must be an absolute https URL.");
        }

        return trimmed;
    }

    /// <summary>
    /// An absolute http(s) URL with no query or fragment. http is stored so a dev setup with
    /// Cds:AllowInsecureCallback can name a local FHIR server; the callback client still refuses http unless that
    /// flag is on.
    /// </summary>
    private static string? OptionalFhirBaseUrl(string? value)
    {
        var trimmed = Optional(value, UrlMaxLength, "FHIR base URL");
        if (trimmed is null)
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new BusinessRuleException("FHIR base URL must be an absolute http(s) URL (https outside development).");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || trimmed.Contains('?') || trimmed.Contains('#'))
        {
            throw new BusinessRuleException("FHIR base URL must not have a query string or fragment.");
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>Only the shape is checked here (an object with a non-empty "keys" array); whether each key is
    /// usable for RS256/RS384/ES256/ES384 is decided when a call is verified.</summary>
    private static string? OptionalJwks(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("keys", out var keys)
                || keys.ValueKind != JsonValueKind.Array
                || keys.GetArrayLength() == 0)
            {
                throw new BusinessRuleException("The pasted JWKS must be a JSON object with a non-empty \"keys\" array.");
            }
        }
        catch (JsonException)
        {
            throw new BusinessRuleException("The pasted JWKS is not valid JSON.");
        }

        return trimmed;
    }
}
