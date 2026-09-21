using System.ComponentModel.DataAnnotations;
using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using FHIRBridge.LicenseServer.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Pages.Licenses;

public sealed class CreateModel : PageModel
{
    /// <summary>Fixed number of hospital-row inputs rendered on the form. Pragmatic stand-in for a fully
    /// dynamic add/remove list without a JS framework — see Create.cshtml's "Add another hospital" button,
    /// which just reveals the next hidden row. Blank rows are ignored on submit.</summary>
    public const int MaxHospitalRows = 10;

    /// <summary>Only the SourceSystemType values that have a real, registered connector in the main
    /// FHIRBridge repo (src/Runtime/FHIRBridge.Runtime.Infrastructure/DependencyInjection.cs: only
    /// EpicFhirSourceClient, AthenahealthFhirSourceClient, EClinicalWorksFhirSourceClient are wired up) —
    /// the enum itself declares more (Cerner, Allscripts, GenericFhir, Hl7v2, MeditechGreenfield, NewEHR,
    /// NewEHRTwo, Sample), but a license shouldn't be able to allow-list a source type customers can't
    /// actually configure. Keep this in sync with the portal's own narrowed SOURCE_TYPE_OPTIONS
    /// (license-dev-mint.component.ts).</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> SourceSystemTypes = new[]
    {
        ("Epic", "Epic"),
        ("Athenahealth", "Athenahealth"),
        ("Healow", "Healow (eClinicalWorks)"),
    };

    /// <summary>The FHIR resource type names from the main FHIRBridge repo's
    /// <c>src/FHIRBridge.Domain/Fhir/SupportedFhirResourceTypes.cs</c> (<c>All</c>), hardcoded here since this
    /// standalone project has no reference into the main repo. Unlike SourceSystemTypes/DestinationTypes
    /// above, this is NOT narrowed down — every resource type here is genuinely configurable today (Mapping
    /// Profiles and the Destination Wizard both offer the complete set). Keep this in sync with the
    /// portal's own RESOURCE_TYPE_OPTIONS (license-dev-mint.component.ts).</summary>
    public static readonly IReadOnlyList<string> ResourceTypes = new[]
    {
        "Patient", "Practitioner", "Observation", "Condition", "MedicationRequest", "MedicationAdministration",
        "AllergyIntolerance", "Encounter", "DiagnosticReport", "Procedure", "ServiceRequest", "Immunization",
        "Appointment", "Binary", "CarePlan", "CareTeam", "Communication", "CommunicationRequest", "Device",
        "DocumentReference", "FamilyMemberHistory", "Goal", "ImagingStudy", "Location", "Medication",
        "MedicationDispense", "MedicationStatement", "Organization", "PractitionerRole", "Provenance",
        "Questionnaire", "QuestionnaireResponse", "RelatedPerson", "Schedule", "Slot", "Specimen", "Task",
        "Account", "AdverseEvent", "BodyStructure", "Claim", "Consent", "Contract", "Coverage", "DeviceRequest",
        "DeviceUseStatement", "Endpoint", "EpisodeOfCare", "ExplanationOfBenefit", "Flag", "Group",
        "ImmunizationRecommendation", "List", "Media", "NutritionOrder", "RequestGroup", "ResearchStudy",
        "ResearchSubject", "Substance",
    };

    /// <summary>Only the destination types offered as a real, working create-flow card in the portal
    /// today (CREATE_TYPES in destination-connection-dialog.component.ts) — the DestinationType enum
    /// declares 26 members, most Phase 2+/stub. Keep this in sync with the portal's own narrowed
    /// DESTINATION_TYPE_OPTIONS (license-dev-mint.component.ts).</summary>
    public static readonly IReadOnlyList<string> DestinationTypes = new[]
    {
        "SqlServer", "PostgreSql", "MySql", "Mongo", "BlobStorage", "Csv", "FhirRepository", "Medplum",
        "AzureFhirService", "DataLakeWebhook",
    };

    private readonly LicenseServerDbContext _db;
    private readonly LicenseTokenMinter _minter;

    public CreateModel(LicenseServerDbContext db, LicenseTokenMinter minter)
    {
        _db = db;
        _minter = minter;
        Hospitals = Enumerable.Range(0, MaxHospitalRows).Select(_ => new HospitalRowInput()).ToList();
    }

    /// <summary>Set when arriving from a "Create License" link on Pages/LicenseRequests/Index — carries the
    /// pending LicenseRequest through both the GET (to prefill the form) and the POST (to embed its
    /// UniqueKey as the requestKey claim and mark it fulfilled once minting succeeds).</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? RequestId { get; set; }

    /// <summary>The linked request's contact details, shown read-only above the form when
    /// <see cref="RequestId"/> is set — loaded on GET, re-loaded on a failed POST so it survives
    /// validation round-trips.</summary>
    public LicenseRequest? LinkedRequest { get; private set; }

    [BindProperty]
    [Required]
    public string CustomerId { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    public string CustomerName { get; set; } = string.Empty;

    [BindProperty]
    public string Edition { get; set; } = "standard";

    [BindProperty]
    [Required]
    [DataType(DataType.Date)]
    public DateTime? ExpiresOn { get; set; }

    [BindProperty]
    public int? MaxUsers { get; set; }

    [BindProperty]
    public int? MaxWorkflows { get; set; }

    [BindProperty]
    public int? MaxSourceConnections { get; set; }

    [BindProperty]
    public int? MaxProcessedRecordsPerMonth { get; set; }

    [BindProperty]
    public int? MaxSuccessfulWorkflowExecutionsPerMonth { get; set; }

    [BindProperty]
    public int? ActivationWindowMinutes { get; set; }

    [BindProperty]
    public List<string> SelectedSourceTypes { get; set; } = new();

    [BindProperty]
    public List<string> SelectedResourceTypes { get; set; } = new();

    [BindProperty]
    public List<string> SelectedDestinationTypes { get; set; } = new();

    [BindProperty]
    public List<HospitalRowInput> Hospitals { get; set; }

    // Nullable (rather than a non-nullable string default) so ASP.NET Core's implicit "required" validation
    // for non-nullable reference type properties doesn't reject a blank Features field - this is genuinely
    // optional (none typed = no features), same as FeaturesCsv's own usage below already assumed.
    [BindProperty]
    public string? FeaturesCsv { get; set; }

    public string? MintedToken { get; private set; }

    public string? MintedClaimsJson { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        if (RequestId is not { } requestId)
        {
            return;
        }

        LinkedRequest = await _db.LicenseRequests.FindAsync(requestId);
        if (LinkedRequest is null)
        {
            return;
        }

        // Pre-fills exactly the fields the requesting install collected — CustomerId is deliberately left
        // for the admin to type (it's an internal identifier this server has no equivalent of on the
        // request side, e.g. a CRM/contract id), same as CustomerName being editable even though it starts
        // pre-filled: the client's own typed name may not be the exact legal/display name to put on record.
        CustomerName = LinkedRequest.ClientName;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (RequestId is { } requestId)
        {
            LinkedRequest = await _db.LicenseRequests.FindAsync(requestId);
        }

        if (!ModelState.IsValid || ExpiresOn is null)
        {
            ErrorMessage = "Customer Id, Customer Name and Expires are required.";
            return Page();
        }

        var hospitals = Hospitals
            .Where(h => !string.IsNullOrWhiteSpace(h.Vendor) && !string.IsNullOrWhiteSpace(h.BaseUrl))
            .Select(h => new AllowedHospital(h.Vendor!.Trim(), h.BaseUrl!.Trim(), string.IsNullOrWhiteSpace(h.DisplayName) ? null : h.DisplayName!.Trim()))
            .ToList();

        var features = (FeaturesCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var fields = new LicenseFields
        {
            CustomerId = CustomerId.Trim(),
            CustomerName = CustomerName.Trim(),
            Edition = string.IsNullOrWhiteSpace(Edition) ? "standard" : Edition.Trim(),
            ExpiresUtc = DateTime.SpecifyKind(ExpiresOn.Value.Date, DateTimeKind.Utc),
            // Blank form field -> null -> LicenseFields.Unlimited (-1). An admin may also type -1 explicitly;
            // both mean the same thing.
            MaxUsers = MaxUsers ?? LicenseFields.Unlimited,
            MaxWorkflows = MaxWorkflows ?? LicenseFields.Unlimited,
            MaxSourceConnections = MaxSourceConnections ?? LicenseFields.Unlimited,
            MaxProcessedRecordsPerMonth = MaxProcessedRecordsPerMonth ?? LicenseFields.Unlimited,
            MaxSuccessfulWorkflowExecutionsPerMonth = MaxSuccessfulWorkflowExecutionsPerMonth ?? LicenseFields.Unlimited,
            AllowedSourceTypes = SelectedSourceTypes.Count > 0 ? SelectedSourceTypes : null,
            AllowedHospitals = hospitals.Count > 0 ? hospitals : null,
            Features = features,
            AllowedResourceTypes = SelectedResourceTypes.Count > 0 ? SelectedResourceTypes : null,
            AllowedDestinationTypes = SelectedDestinationTypes.Count > 0 ? SelectedDestinationTypes : null,
            ActivationWindowMinutes = ActivationWindowMinutes,
            RequestKey = LinkedRequest?.UniqueKey,
        };

        var nowUtc = DateTime.UtcNow;
        var (token, claimsJson) = _minter.Mint(fields, nowUtc);

        var issuedLicense = new IssuedLicense
        {
            Id = Guid.NewGuid(),
            CustomerId = fields.CustomerId,
            CustomerName = fields.CustomerName,
            Edition = fields.Edition,
            IssuedAtUtc = nowUtc,
            ExpiresUtc = fields.ExpiresUtc,
            MaxUsers = fields.MaxUsers,
            MaxWorkflows = fields.MaxWorkflows,
            MaxSourceConnections = fields.MaxSourceConnections,
            MaxProcessedRecordsPerMonth = fields.MaxProcessedRecordsPerMonth,
            MaxSuccessfulWorkflowExecutionsPerMonth = fields.MaxSuccessfulWorkflowExecutionsPerMonth,
            ActivationWindowMinutes = fields.ActivationWindowMinutes,
            AllowedSourceTypesSummary = fields.AllowedSourceTypes is null ? "(all)" : string.Join(", ", fields.AllowedSourceTypes),
            FeaturesSummary = fields.Features.Count == 0 ? "(none)" : string.Join(", ", fields.Features),
            AllowedResourceTypesSummary = fields.AllowedResourceTypes is null ? "(all)" : string.Join(", ", fields.AllowedResourceTypes),
            AllowedDestinationTypesSummary = fields.AllowedDestinationTypes is null ? "(all)" : string.Join(", ", fields.AllowedDestinationTypes),
            ClaimsJson = claimsJson,
            Token = token,
            RequestHost = LinkedRequest?.RequestHost,
        };
        _db.IssuedLicenses.Add(issuedLicense);

        if (LinkedRequest is not null)
        {
            LinkedRequest.FulfilledAtUtc = nowUtc;
            LinkedRequest.FulfilledIssuedLicenseId = issuedLicense.Id;
        }

        await _db.SaveChangesAsync();

        MintedToken = token;
        MintedClaimsJson = claimsJson;

        return Page();
    }

    public sealed class HospitalRowInput
    {
        public string? Vendor { get; set; }

        public string? BaseUrl { get; set; }

        public string? DisplayName { get; set; }
    }
}
