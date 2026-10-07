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
        bool liveWriteSupported = false,
        bool requiresVendorActivation = false,
        bool createsHolderEncounter = false,
        bool requiresVariantOptIn = false,
        bool requiresPatient = true,
        bool requiresTargetReferences = false)
    {
        ResourceType = SupportedFhirResourceTypes.Normalize(resourceType);
        Operations = operations;
        VendorApiId = vendorApiId;
        Variant = variant;
        RequiresEncounter = requiresEncounter;
        OptInOnly = optInOnly;
        AllowedApplicationTypes = allowedApplicationTypes;
        LiveWriteSupported = liveWriteSupported;
        RequiresVendorActivation = requiresVendorActivation;
        CreatesHolderEncounter = createsHolderEncounter;
        RequiresVariantOptIn = requiresVariantOptIn;
        RequiresPatient = requiresPatient;
        RequiresTargetReferences = requiresTargetReferences;
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

    /// <summary>The vendor's API for this write is not open to every client: it is contracted and activated per
    /// practice (eClinicalWorks), or a proprietary API product the practice must enable (athenaOne). Live only when the
    /// write connection says so (<c>SourceConnection.VendorWriteApisActivated</c>); until then every run of the type is
    /// a dry run, reported as <c>vendor-activation-required</c>.</summary>
    public bool RequiresVendorActivation { get; }

    /// <summary>The API files the record on an encounter the bridge must create first (eClinicalWorks medical and
    /// surgical history go on an open telephone encounter). Written only when the destination opts in, one encounter
    /// per patient per run, created only when at least one such record is actually sent.</summary>
    public bool CreatesHolderEncounter { get; }

    /// <summary>One flavour of a type that other flavours already cover (Epic files lines, drains and airways through a
    /// different Observation API than vital signs). Selecting the resource type is not enough: a record goes to this
    /// variant only when the destination also lists the variant (<c>dest_enabledVariants</c>), so turning on a new API
    /// never changes what an existing destination sends. Otherwise reported as <c>variant-not-enabled</c>.</summary>
    public bool RequiresVariantOptIn { get; }

    /// <summary>The record is filed against a patient, which the writer resolves in the EHR first. False for an API whose
    /// record is not about one patient (Epic non-patient documents) or names its context some other way (Epic
    /// patient-entered questionnaires answer an assignment, not a patient).</summary>
    public bool RequiresPatient { get; }

    /// <summary>The API needs references to records that already exist in the TARGET EHR and that the writer cannot
    /// resolve (a questionnaire and its question ids, the referral a message belongs to, the report an image finding is
    /// about). Only a CSV / SQL Table source, whose template is written for the target, can carry them; from any other
    /// source the record is skipped as <c>target-references-unmappable</c>, because a source system's ids would point at
    /// nothing, or at the wrong record, in the target.</summary>
    public bool RequiresTargetReferences { get; }

    /// <summary>True when a destination that is not a dry run sends this type for real over a connection whose vendor
    /// write APIs are (<paramref name="vendorWriteApisActivated"/>) or are not activated.</summary>
    public bool IsLive(bool vendorWriteApisActivated) =>
        LiveWriteSupported && (!RequiresVendorActivation || vendorWriteApisActivated);

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
        IReadOnlySet<string> alreadyAtTargetOutcomeCodes,
        string? proprietaryApiScope = null)
    {
        Vendor = vendor;
        Capabilities = capabilities;
        SupportsPatientMatch = supportsPatientMatch;
        RequestsScopeOnTokenRequest = requestsScopeOnTokenRequest;
        AlreadyAtTargetOutcomeCodes = alreadyAtTargetOutcomeCodes;
        ProprietaryApiScope = proprietaryApiScope;
    }

    /// <summary>The scope that grants the vendor's proprietary write API (athenaOne: <c>athena/service/Athenanet.MDP.*</c>),
    /// which a write connection requests alongside its FHIR scopes. Types that need vendor activation go through that
    /// API, so their granted-scope check looks for this scope, not a FHIR create scope. Null for a FHIR-only
    /// vendor.</summary>
    public string? ProprietaryApiScope { get; }

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
