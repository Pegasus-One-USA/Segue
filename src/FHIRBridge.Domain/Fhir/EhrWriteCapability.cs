using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Domain.Fhir;

/// <summary>
/// One FHIR resource type an EHR accepts writes for, with the rules that decide whether a record may be sent.
/// Entries live in <see cref="EhrWriteCapabilities"/>; nothing else should construct them.
/// </summary>
public sealed class EhrWriteCapability
{
    public EhrWriteCapability(
        string resourceType,
        IReadOnlySet<EhrWriteOperation> operations,
        string vendorApiId,
        string? variant,
        bool requiresEncounter,
        bool optInOnly,
        IReadOnlySet<ApplicationType> allowedApplicationTypes,
        bool liveWriteSupported = false)
    {
        ResourceType = SupportedFhirResourceTypes.Normalize(resourceType);
        Operations = operations;
        VendorApiId = vendorApiId;
        Variant = variant;
        RequiresEncounter = requiresEncounter;
        OptInOnly = optInOnly;
        AllowedApplicationTypes = allowedApplicationTypes;
        LiveWriteSupported = liveWriteSupported;
    }

    /// <summary>Canonical FHIR resource type name, as in <see cref="SupportedFhirResourceTypes.All"/>.</summary>
    public string ResourceType { get; }

    public IReadOnlySet<EhrWriteOperation> Operations { get; }

    /// <summary>The vendor's own API identifier, e.g. Epic's specification id, so a log line or ledger row can be
    /// traced to the exact API contract it was shaped for.</summary>
    public string VendorApiId { get; }

    /// <summary>Which flavour of the resource the API accepts when the vendor splits one FHIR type across several
    /// APIs (Epic files vital signs and lines/drains/airways through different Observation APIs). A record of a
    /// different flavour is skipped, never sent. Null when the API takes the resource type as a whole.</summary>
    public string? Variant { get; }

    /// <summary>The record must point at an existing encounter of the same patient in the EHR. EHR R4 APIs cannot
    /// create encounters, so a record without an eligible one is skipped.</summary>
    public bool RequiresEncounter { get; }

    /// <summary>Written only when the destination explicitly opts in (Patient creation).</summary>
    public bool OptInOnly { get; }

    /// <summary>SMART application types whose tokens the vendor accepts for this write.</summary>
    public IReadOnlySet<ApplicationType> AllowedApplicationTypes { get; }

    /// <summary>The live send path for this API has been built and verified, so a destination that selects this type
    /// and is not a dry run sends it. False means every run of this type stays a dry run whatever the destination
    /// asks for. Who may take a destination off dry run, and run it, is decided by the EHR Write-Back
    /// permissions.</summary>
    public bool LiveWriteSupported { get; }

    public bool Supports(EhrWriteOperation operation) => Operations.Contains(operation);
}

/// <summary>
/// Vendor-wide write-back facts: the per-resource capabilities plus the behaviour that applies to every write on that
/// vendor's connections.
/// </summary>
public sealed class EhrWriteVendorProfile
{
    public EhrWriteVendorProfile(
        SourceSystemType vendor,
        IReadOnlyList<EhrWriteCapability> capabilities,
        bool supportsPatientMatch,
        bool requestsScopeOnTokenRequest,
        IReadOnlySet<string> alreadyAtTargetOutcomeCodes)
    {
        Vendor = vendor;
        Capabilities = capabilities;
        SupportsPatientMatch = supportsPatientMatch;
        RequestsScopeOnTokenRequest = requestsScopeOnTokenRequest;
        AlreadyAtTargetOutcomeCodes = alreadyAtTargetOutcomeCodes;
    }

    /// <summary>Vendor OperationOutcome codes that mean "this record is already in the EHR", each either a bare code
    /// or "code|expression" when the code means that only for one element. A create refused with one of them is
    /// recorded as already at target, never retried.</summary>
    public IReadOnlySet<string> AlreadyAtTargetOutcomeCodes { get; }

    public bool IsAlreadyAtTarget(string? vendorCode, string? expression) =>
        vendorCode is not null
        && (AlreadyAtTargetOutcomeCodes.Contains(vendorCode)
            || (expression is not null && AlreadyAtTargetOutcomeCodes.Contains($"{vendorCode}|{expression}")));

    public SourceSystemType Vendor { get; }

    public IReadOnlyList<EhrWriteCapability> Capabilities { get; }

    /// <summary>The vendor exposes <c>Patient/$match</c> with certain-only matching, so patients can be resolved by
    /// demographics. It is an operation, not a write, which is why it is not an <see cref="EhrWriteOperation"/>.</summary>
    public bool SupportsPatientMatch { get; }

    /// <summary>False when the vendor ignores the token request's <c>scope</c> and grants whatever APIs are
    /// registered on the client id (Epic). The write path then sends no scope and checks the granted scope that comes
    /// back instead.</summary>
    public bool RequestsScopeOnTokenRequest { get; }
}
