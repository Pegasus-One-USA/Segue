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
public sealed class EhrEndpoint : AuditableChildEntity<Guid>
{
    private EhrEndpoint()
    {
    }

    public EhrEndpoint(SourceSystemType vendor, string vendorEndpointId, string name, string fhirBaseUrl, string formatType, string status)
    {
        Id = Guid.NewGuid();
        Vendor = vendor;
        VendorEndpointId = vendorEndpointId;
        Name = name;
        FhirBaseUrl = fhirBaseUrl;
        FormatType = formatType;
        Status = status;
    }

    /// <summary>Which EHR vendor's directory this row came from (the vendor axis — same enum SourceConnection uses).</summary>
    public SourceSystemType Vendor { get; private set; }

    /// <summary>The vendor's own id for this endpoint — lets each vendor's seeder detect already-imported rows on re-sync.</summary>
    public string VendorEndpointId { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string FhirBaseUrl { get; private set; } = default!;
    public string FormatType { get; private set; } = default!;
    public string Status { get; private set; } = default!;
}
