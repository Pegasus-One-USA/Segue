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
    //
    // Phase 7 adds the rest of Epic's incoming R4 create APIs, each a variant the destination must enable on top of
    // selecting its type, so no existing destination starts sending something new. They were held back because no Epic
    // write can be undone; they are rehearsed on a Generic FHIR test server first (dest_testAsVendor). Specs:
    // fhir.epic.com/Specifications/Api?id=<id>. Three of them file against records that must already exist in the
    // target Epic (a referral, an imaging report, a questionnaire assignment), so they take a CSV / SQL Table source
    // only. Not here: CriteriaReview.Create (1009) and ReviewCollection.Create (1026), Epic proprietary
    // (non-FHIR) utilization-management APIs.
    private static readonly EhrWriteVendorProfile Epic = new(
        SourceSystemType.Epic,
        [
            new("AllergyIntolerance", CreateOnly, "945", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true),
            new("BodyStructure", CreateOnly, "11040", EhrWriteVariants.RadiotherapyVolume, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true),
            // Community-resource referral messages: basedOn names the referral's ServiceRequest in the target Epic.
            new("Communication", CreateOnly, "10090", EhrWriteVariants.CommunityResourceMessage, requiresEncounter: true, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true, requiresTargetReferences: true),
            new("Condition", CreateOnly, "949", EhrWriteVariants.ProblemListItem, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true),
            new("DocumentReference", CreateOnly, "1046", EhrWriteVariants.ClinicalNote, requiresEncounter: true, optInOnly: false, BackendOnly, liveWriteSupported: true),
            // Scan metadata. Epic: "designed for scanning integrations through the Hyperdrive Scan Acquisition Workflow.
            // It cannot be called outside of this workflow."
            new("DocumentReference", CreateOnly, "10050", EhrWriteVariants.DocumentInformation, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true),
            // Tapestry business documents linked to a group, account, CRM contact, vendor or contract: no patient.
            new("DocumentReference", CreateOnly, "10303", EhrWriteVariants.NonPatientDocument, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true, requiresPatient: false, requiresTargetReferences: true),
            new("Observation", CreateOnly, "963", EhrWriteVariants.VitalSigns, requiresEncounter: true, optInOnly: false, BackendOnly, liveWriteSupported: true),
            new("Observation", CreateOnly, "962", EhrWriteVariants.LinesDrainsAirways, requiresEncounter: true, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true),
            // Excessive-radiation CT findings: focus names the imaging DiagnosticReport in the target Epic.
            new("Observation", CreateOnly, "11224", EhrWriteVariants.ImagingCharacteristics, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true, requiresTargetReferences: true),
            new("Patient", CreateOnly, "930", variant: null, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true),
            new("Procedure", CreateOnly, "11048", EhrWriteVariants.RadiotherapySummary, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true),
            // Answers a patient-entered questionnaire Epic already assigned: subject is the appointment or questionnaire
            // series, questionnaire and linkIds are Epic ids.
            new("QuestionnaireResponse", CreateOnly, "10023", EhrWriteVariants.PatientEnteredQuestionnaire, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true, requiresPatient: false, requiresTargetReferences: true),
            new("ServiceRequest", CreateOnly, "11044", EhrWriteVariants.RadiotherapySummary, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVariantOptIn: true),
        ],
        supportsPatientMatch: true,
        requestsScopeOnTokenRequest: false,
        // 59141 "An attempt was made to create a duplicate record" (documented for an allergy already on the chart).
        // 59189 is the generic "Failed to file the reading"; only with expression code/instant does it mean "Reading
        // already exists" (a repeated vital, seen live), so it is matched together with that expression.
        alreadyAtTargetOutcomeCodes: new HashSet<string>(StringComparer.Ordinal) { "59141", "59189|code/instant" });

    // eClinicalWorks (Healow), 2026-10-06, from eCW's published Create API documentation
    // (fhir.eclinicalworks.com/ecwopendev/documentation/create-resources). eCW's certified FHIR server lists only
    // QuestionnaireResponse create; every clinical write is a CONTRACTED API (interop@eclinicalworks.com), activated per
    // practice and per build, and sent as a FHIR transaction Bundle to the base URL rather than a POST to /{type}. So
    // each clinical type is live-capable in code but sends only once the write connection says the practice has the
    // APIs activated (requiresVendorActivation). Most creates land in eCW's "App Data" tab for a user to reconcile.
    // eCW has no Patient/$match: patients resolve by identifier, then through the MPI. See
    // docs/backend/20-epic-r4-write-back.md section 13.
    private static readonly EhrWriteVendorProfile Healow = new(
        SourceSystemType.Healow,
        [
            new("AllergyIntolerance", CreateOnly, "ecw-allergyintolerance-create", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Condition", CreateOnly, "ecw-condition-problems-create", EhrWriteVariants.ProblemListItem, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Condition", CreateOnly, "ecw-condition-encounter-diagnosis-create", EhrWriteVariants.EncounterDiagnosis, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Condition", CreateOnly, "ecw-condition-medical-history-create", EhrWriteVariants.MedicalHistory, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true, createsHolderEncounter: true),
            new("DocumentReference", CreateOnly, "ecw-documentreference-clinical-notes-create", EhrWriteVariants.ClinicalNote, requiresEncounter: true, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Immunization", CreateOnly, "ecw-immunization-create", EhrWriteVariants.HistoricalImmunization, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            // Source medication records of either type are filed as an eCW MedicationStatement (reconciliation).
            new("MedicationRequest", CreateOnly, "ecw-medicationstatement-reconciliation-create", EhrWriteVariants.MedicationList, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("MedicationStatement", CreateOnly, "ecw-medicationstatement-reconciliation-create", EhrWriteVariants.MedicationList, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            // eCW vitals carry no encounter: effectiveDateTime is matched to an appointment, else filed under Vitals Notes.
            new("Observation", CreateOnly, "ecw-observation-vitals-create", EhrWriteVariants.VitalSigns, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Patient", CreateOnly, "ecw-patient-create", variant: null, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Procedure", CreateOnly, "ecw-procedure-surgical-history-create", EhrWriteVariants.SurgicalHistory, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true, createsHolderEncounter: true),
            // The one certified (non-contracted) write. It needs eCW's Backend-Questionnaire app and an HL7 v2 payload,
            // neither of which this product has, so it stays dry-run-only.
            new("QuestionnaireResponse", CreateOnly, "ecw-questionnaireresponse-create", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly),
        ],
        supportsPatientMatch: false,
        requestsScopeOnTokenRequest: true,
        // 202 INVALID_PATIENT_ALREADY_EXIST: the patient is there; never retried as a create.
        alreadyAtTargetOutcomeCodes: new HashSet<string>(StringComparer.Ordinal) { "202" });

    // athenahealth, 2026-10-06. The certified FHIR R4 API creates only QuestionnaireResponse (kept, dry-run-only until
    // verified). Every clinical write goes through the proprietary athenaOne REST API (/v1/{practiceid}/...), which the
    // write connection's token reaches with the athena/service/Athenanet.MDP.* scope. Those types are live-capable in
    // code and send only once the connection says the practice has the athenaOne APIs enabled. athena has no
    // Patient/$match: patients resolve by identifier, then through the MPI. Vitals go on an open or in-review encounter;
    // lab results, notes, medications and immunizations are chart documents or entries that need no encounter. See
    // docs/backend/20-epic-r4-write-back.md section 13.
    private static readonly EhrWriteVendorProfile Athenahealth = new(
        SourceSystemType.Athenahealth,
        [
            new("AllergyIntolerance", CreateOnly, "athenaone-chart-allergies-put", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Condition", CreateOnly, "athenaone-chart-problems-post", EhrWriteVariants.ProblemListItem, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("DocumentReference", CreateOnly, "athenaone-documents-clinicaldocument-post", EhrWriteVariants.ClinicalNote, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Immunization", CreateOnly, "athenaone-chart-vaccines-post", EhrWriteVariants.HistoricalImmunization, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("MedicationRequest", CreateOnly, "athenaone-chart-medications-post", EhrWriteVariants.MedicationList, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("MedicationStatement", CreateOnly, "athenaone-chart-medications-post", EhrWriteVariants.MedicationList, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Observation", CreateOnly, "athenaone-encounter-vitals-post", EhrWriteVariants.VitalSigns, requiresEncounter: true, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Observation", CreateOnly, "athenaone-documents-labresult-post", EhrWriteVariants.LaboratoryResult, requiresEncounter: false, optInOnly: false, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("Patient", CreateOnly, "athenaone-patients-post", variant: null, requiresEncounter: false, optInOnly: true, BackendOnly, liveWriteSupported: true, requiresVendorActivation: true),
            new("QuestionnaireResponse", CreateOnly, "athena-questionnaireresponse-create", variant: null, requiresEncounter: false, optInOnly: false, BackendOnly),
        ],
        supportsPatientMatch: false,
        requestsScopeOnTokenRequest: true,
        // athenaOne 409: "a conflict ... e.g. duplicate data where none is allowed".
        alreadyAtTargetOutcomeCodes: new HashSet<string>(StringComparer.Ordinal) { "athena-409" },
        proprietaryApiScope: "athena/service/Athenanet.MDP.*");

    /// <summary>Resource types a Generic FHIR R4 server takes as plain creates.</summary>
    public static readonly IReadOnlyList<string> GenericFhirResourceTypes =
    [
        "AllergyIntolerance", "BodyStructure", "CarePlan", "Communication", "Condition", "DiagnosticReport",
        "DocumentReference", "Goal", "Immunization", "MedicationRequest", "MedicationStatement", "Observation",
        "Patient", "Procedure", "QuestionnaireResponse", "ServiceRequest",
    ];

    // Generic FHIR R4 (HAPI, Aidbox, a hospital's own server), Phase 7: any standard resource as a plain create. The
    // server's own rules are not known, so nothing needs an encounter; references other than the patient are not
    // carried over (a source id names nothing on another server). Patients resolve by identifier, then $match where the
    // server offers it. The same connection is the test server for "test as" runs, which use the TESTED vendor's
    // profile instead of this one.
    private static readonly EhrWriteVendorProfile GenericFhir = new(
        SourceSystemType.GenericFhir,
        GenericFhirResourceTypes
            .Select(type => new EhrWriteCapability(
                type, CreateOnly, "fhir-r4-create", variant: null, requiresEncounter: false,
                optInOnly: type == "Patient", BackendOnly, liveWriteSupported: true))
            .ToList(),
        supportsPatientMatch: true,
        requestsScopeOnTokenRequest: true,
        alreadyAtTargetOutcomeCodes: new HashSet<string>(StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<SourceSystemType, EhrWriteVendorProfile> ByVendor =
        new Dictionary<SourceSystemType, EhrWriteVendorProfile>
        {
            [SourceSystemType.Epic] = Epic,
            [SourceSystemType.Healow] = Healow,
            [SourceSystemType.Athenahealth] = Athenahealth,
            [SourceSystemType.GenericFhir] = GenericFhir,
        };

    /// <summary>Vendors a Generic FHIR server can stand in for in a test run (<c>dest_testAsVendor</c>).</summary>
    public static readonly IReadOnlyList<SourceSystemType> TestableVendors =
        [SourceSystemType.Epic, SourceSystemType.Healow, SourceSystemType.Athenahealth];

    /// <summary>The connection type that serves as the test server for <see cref="TestableVendors"/>.</summary>
    public const SourceSystemType TestServerType = SourceSystemType.GenericFhir;

    /// <summary>Every variant a destination can enable (<c>dest_enabledVariants</c>), across vendors.</summary>
    public static IReadOnlySet<string> OptInVariants { get; } = ByVendor.Values
        .SelectMany(profile => profile.Capabilities)
        .Where(capability => capability.RequiresVariantOptIn && capability.Variant is not null)
        .Select(capability => capability.Variant!)
        .ToHashSet(StringComparer.Ordinal);

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

    /// <summary>The first capability for the type. A vendor may list one type several times, once per variant (eCW
    /// files problems, encounter diagnoses and medical history through three Condition APIs); see
    /// <see cref="FindAll"/>.</summary>
    public static EhrWriteCapability? Find(SourceSystemType vendor, string? resourceType) =>
        FindAll(vendor, resourceType).FirstOrDefault();

    /// <summary>Every capability for the type, in table order.</summary>
    public static IReadOnlyList<EhrWriteCapability> FindAll(SourceSystemType vendor, string? resourceType) =>
        string.IsNullOrWhiteSpace(resourceType)
            ? []
            : For(vendor).Where(capability =>
                string.Equals(capability.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase)).ToList();

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
    public const string EncounterDiagnosis = "encounter-diagnosis";
    public const string MedicalHistory = "medical-history";
    public const string SurgicalHistory = "surgical-history";
    public const string LaboratoryResult = "laboratory-result";
    public const string HistoricalImmunization = "historical-immunization";
    public const string MedicationList = "medication-list";
    public const string LinesDrainsAirways = "lines-drains-airways";
    public const string ImagingCharacteristics = "dicom-image-characteristics";
    public const string RadiotherapyVolume = "radiotherapy-volume";
    public const string RadiotherapySummary = "external-radiotherapy-summary";
    public const string DocumentInformation = "document-information";
    public const string NonPatientDocument = "non-patient-document";
    public const string CommunityResourceMessage = "community-resource-message";
    public const string PatientEnteredQuestionnaire = "patient-entered-questionnaire";
}
