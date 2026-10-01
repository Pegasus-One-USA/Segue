using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A known EHR client-organization FHIR endpoint, imported from a vendor's public endpoint directory (e.g. Epic's
/// https://open.epic.com/Endpoints/R4). <see cref="Vendor"/> distinguishes which directory a row came from, since
/// multiple vendor-specific seeders (see IEhrEndpointDirectorySeeder) share this one table. FormatType is hardcoded
/// per seeder today ("R4" for Epic's directory) — a future directory serving a different FHIR version would set its
/// own FormatType value.
/// </summary>
public sealed class EhrEndpoint : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private EhrEndpoint()
    {
    }

    public EhrEndpoint(
        SourceSystemType vendor,
        string vendorEndpointId,
        string name,
        string fhirBaseUrl,
        string formatType,
        string status,
        EhrEndpointType endpointType = EhrEndpointType.MyChart,
        string? tokenEndpoint = null,
        string? clientId = null,
        string? keyId = null,
        string? jwksUrl = null,
        string? practiceId = null)
    {
        Id = Guid.NewGuid();
        Vendor = vendor;
        VendorEndpointId = vendorEndpointId;
        Name = name;
        FhirBaseUrl = fhirBaseUrl;
        FormatType = formatType;
        Status = status;
        EndpointType = endpointType;
        TokenEndpoint = tokenEndpoint;
        ClientId = clientId;
        KeyId = keyId;
        JwksUrl = jwksUrl;
        PracticeId = practiceId;
    }

    public void Update(
        SourceSystemType vendor,
        string vendorEndpointId,
        string name,
        string fhirBaseUrl,
        string formatType,
        string status,
        EhrEndpointType endpointType = EhrEndpointType.MyChart,
        string? tokenEndpoint = null,
        string? clientId = null,
        string? keyId = null,
        string? jwksUrl = null,
        string? practiceId = null)
    {
        Vendor = vendor;
        VendorEndpointId = vendorEndpointId;
        Name = name;
        FhirBaseUrl = fhirBaseUrl;
        FormatType = formatType;
        Status = status;
        EndpointType = endpointType;
        TokenEndpoint = tokenEndpoint;
        ClientId = clientId;
        KeyId = keyId;
        JwksUrl = jwksUrl;
        PracticeId = practiceId;
    }

    /// <summary>Which EHR vendor's directory this row came from (the vendor axis — same enum SourceConnection uses).</summary>
    public SourceSystemType Vendor { get; private set; }

    /// <summary>The vendor's own id for this endpoint — lets each vendor's seeder detect already-imported rows on re-sync.</summary>
    public string VendorEndpointId { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;
    public string FhirBaseUrl { get; private set; } = default!;
    public string FormatType { get; private set; } = default!;
    public string Status { get; private set; } = default!;

    /// <summary>Vendor sandbox vs a specific customer's production instance — see <see cref="EhrEndpointType"/>.</summary>
    public EhrEndpointType EndpointType { get; private set; }

    // Per-hospital overrides used ONLY when a run is explicitly started against this endpoint (see
    // EhrEndpointSourceOverride). All optional: a blank value means "keep what the source connection has".
    // FhirBaseUrl (above) is the hospital's base URL override. Secrets are deliberately NOT here — the private key /
    // client secret stay on the source connection.

    /// <summary>Hospital-specific OAuth token endpoint (Epic and Cerner are per-organisation).</summary>
    public string? TokenEndpoint { get; private set; }

    /// <summary>Client id registered for this hospital, when it differs from the source connection's.</summary>
    public string? ClientId { get; private set; }

    /// <summary>Key id (JWT kid) of the public key registered with this hospital.</summary>
    public string? KeyId { get; private set; }

    /// <summary>JWKS URL registered with this hospital. Registration metadata; the runtime signs with KeyId.</summary>
    public string? JwksUrl { get; private set; }

    /// <summary>athenahealth practice id (sent as the ah-practice header).</summary>
    public string? PracticeId { get; private set; }
}
