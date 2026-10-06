using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Domain.Fhir;

/// <summary>
/// Which FHIR resource types each EHR vendor accepts writes for — the single table the destination validator, the
/// workflow executor, the capabilities API and the portal's resource picker all read. A sibling of
/// <see cref="VendorResourceTypeSupport"/>, with the opposite default: a vendor that is not listed can write NOTHING,
/// so <see cref="For(SourceSystemType)"/> returns an empty list, never null.
/// </summary>
public static class EhrWriteCapabilities
{
    /// <summary>Phase 1 accepts Backend Systems tokens only: write-back runs under its own Epic client id registered
    /// as a Backend Systems app (an Epic app cannot change after Ready for Production).</summary>
    private static readonly IReadOnlySet<ApplicationType> BackendOnly =
        new HashSet<ApplicationType> { ApplicationType.Backend };

    private static readonly IReadOnlySet<EhrWriteOperation> CreateOnly =
        new HashSet<EhrWriteOperation> { EhrWriteOperation.Create };

    // Epic: verified 2026-09-30 against each API's raw specification (fhir.epic.com/Specifications/Api?id=<id>),
    // the sandbox R4 CapabilityStatement (Epic August 2026) and live sandbox writes. None of these five APIs supports
    // update, delete, patch, batch or conditional create. See docs/backend/20-epic-r4-write-back.md sections 1 and 6.
    // Live writes: allergies, problems and notes since Phase 2; vitals (open encounter only) and patients (only after
    // $match found no one, and only when the destination opts in) since Phase 3.
    private static readonly EhrWriteVendorProfile Epic = new(
        SourceSystemType.Epic,
        [
            new("AllergyIntolerance", CreateOnly, "945", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true),
            new("Condition", CreateOnly, "949", EhrWriteVariants.ProblemListItem, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true),
            new("DocumentReference", CreateOnly, "1046", EhrWriteVariants.ClinicalNote, requiresEncounter: true, optInOnly: false, BackendOnly, liveWriteSupported: true),
            new("Observation", CreateOnly, "963", EhrWriteVariants.VitalSigns, requiresEncounter: true, optInOnly: false, BackendOnly, liveWriteSupported: true),
            new("Patient", CreateOnly, "930", variant: null, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true),
        ],
        supportsPatientMatch: true,
        requestsScopeOnTokenRequest: false,
        // 59141 "An attempt was made to create a duplicate record" (documented for an allergy already on the chart).
        // 59189 is the generic "Failed to file the reading"; only with expression code/instant does it mean "Reading
        // already exists" (a repeated vital, seen live), so it is matched together with that expression.
        alreadyAtTargetOutcomeCodes: new HashSet<string>(StringComparer.Ordinal) { "59141", "59189|code/instant" });

    // eClinicalWorks (Healow), 2026-10-02. Its certified FHIR server's CapabilityStatement (eCW FHIR Facade 1.6) lists
    // only QuestionnaireResponse create, while its SMART configuration advertises system/*.c for most types: the
    // clinical write APIs are contracted (interop@eclinicalworks.com), practice-activated and build-dependent, and no
    // sandbox has been verified. So every eCW type is dry-run-only (liveWriteSupported false) until a practice
    // sandbox passes the Phase 2 gate. eCW has no Patient/$match, so patients resolve by identifier only, and it
    // honours requested scopes. Notes are left out: eCW files them as a Bundle with an HL7 v2 MDM attachment, a
    // different API shape. See docs/backend/20-epic-r4-write-back.md section 11.
    private static readonly EhrWriteVendorProfile Healow = new(
        SourceSystemType.Healow,
        [
            new("AllergyIntolerance", CreateOnly, "ecw-allergyintolerance-create", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly),
            new("Condition", CreateOnly, "ecw-condition-create", EhrWriteVariants.ProblemListItem, requiresEncounter: false, optInOnly: false, BackendOnly),
            new("Observation", CreateOnly, "ecw-observation-vitals-create", EhrWriteVariants.VitalSigns, requiresEncounter: true, optInOnly: false, BackendOnly),
            new("Patient", CreateOnly, "ecw-patient-create", variant: null, requiresEncounter: false, optInOnly: true, BackendOnly),
            // The one certified (non-contracted) write in eCW's CapabilityStatement.
            new("QuestionnaireResponse", CreateOnly, "ecw-questionnaireresponse-create", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly),
        ],
        supportsPatientMatch: false,
        requestsScopeOnTokenRequest: true,
        // 202 INVALID_PATIENT_ALREADY_EXIST: the patient is there; never retried as a create.
        alreadyAtTargetOutcomeCodes: new HashSet<string>(StringComparer.Ordinal) { "202" });

    // athenahealth, 2026-10-02 (preview, https://api.preview.platform.athenahealth.com/fhir/r4). The certified FHIR R4
    // API is read and search plus one create, QuestionnaireResponse; FamilyMemberHistory $batch-write and MeasureReport
    // $submit-care-gaps are operations, not creates, and are not wired. Allergies, problems, vitals, notes and patients
    // are written through the proprietary athenaOne REST API, outside the FHIR-R4-only scope, so they are not listed.
    // QuestionnaireResponse is dry-run-only until verified; athena has no Patient/$match, so patients resolve by
    // identifier. See docs/backend/20-epic-r4-write-back.md section 12.
    private static readonly EhrWriteVendorProfile Athenahealth = new(
        SourceSystemType.Athenahealth,
        [
            new("QuestionnaireResponse", CreateOnly, "athena-questionnaireresponse-create", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly),
        ],
        supportsPatientMatch: false,
        requestsScopeOnTokenRequest: true,
        alreadyAtTargetOutcomeCodes: new HashSet<string>(StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<SourceSystemType, EhrWriteVendorProfile> ByVendor =
        new Dictionary<SourceSystemType, EhrWriteVendorProfile>
        {
            [SourceSystemType.Epic] = Epic,
            [SourceSystemType.Healow] = Healow,
            [SourceSystemType.Athenahealth] = Athenahealth,
        };

    /// <summary>The vendor's write-back profile, or null when the vendor cannot be written to.</summary>
    public static EhrWriteVendorProfile? VendorProfile(SourceSystemType vendor) =>
        ByVendor.TryGetValue(vendor, out var profile) ? profile : null;

    /// <summary>Every resource type the vendor accepts writes for; empty when it accepts none.</summary>
    public static IReadOnlyList<EhrWriteCapability> For(SourceSystemType vendor) =>
        VendorProfile(vendor)?.Capabilities ?? [];

    /// <summary>String-keyed overload for an unparsed vendor name, e.g. a query parameter. Anything that is not a
    /// defined vendor name (blank, a typo, a number, a comma list) has no write capability.</summary>
    public static IReadOnlyList<EhrWriteCapability> For(string? vendor) =>
        TryParseVendor(vendor, out var parsed) ? For(parsed) : [];

    public static EhrWriteCapability? Find(SourceSystemType vendor, string? resourceType) =>
        string.IsNullOrWhiteSpace(resourceType)
            ? null
            : For(vendor).FirstOrDefault(capability =>
                string.Equals(capability.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase));

    public static bool Supports(SourceSystemType vendor, string? resourceType, EhrWriteOperation operation) =>
        Find(vendor, resourceType)?.Supports(operation) ?? false;

    public static bool HasAnyWriteCapability(SourceSystemType vendor) => For(vendor).Count > 0;

    public static bool TryParseVendor(string? vendor, out SourceSystemType parsed)
    {
        parsed = default;
        var trimmed = vendor?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !char.IsLetter(trimmed[0]) || trimmed.Contains(','))
        {
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }
}

/// <summary>Names for <see cref="EhrWriteCapability.Variant"/>.</summary>
public static class EhrWriteVariants
{
    public const string ProblemListItem = "problem-list-item";
    public const string ClinicalNote = "clinical-note";
    public const string VitalSigns = "vital-signs";
}
