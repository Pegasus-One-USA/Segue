using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HealthAppBackend;

public static class UserRoles
{
    public const string Admin = "Admin";
    public const string Patient = "Patient";
    public const string ProviderStandalone = "ProviderStandalone";
    public const string ProviderInApp = "ProviderInApp";
    public const string BackendSystem = "BackendSystem";
}

// Passwords are stored in plain text — this is dummy demo data, not a real account store.
public sealed class UserEntity
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = UserRoles.Patient;
}

public sealed class HospitalEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int OrganizationId { get; set; }
}

// Demographic columns are populated at ingestion time (see Program.cs POST /api/workflow/run) by flattening
// Payload through PatientFieldExtractor, so reads never need to re-parse the raw FHIR JSON.
public sealed class PatientEntity
{
    public int Id { get; set; }

    // Nullable: rows written directly by a FHIRBridge SQL destination (rather than through this app's own
    // /api/workflow/run ingestion) may leave these unset if the destination's mapping profile doesn't target them.
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? Payload { get; set; }

    public string? FullName { get; set; }
    public string? FirstName { get; set; }
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }
    public string? Gender { get; set; }
    public string? LegalSex { get; set; }
    public string? SexForClinicalUse { get; set; }
    public string? Pronouns { get; set; }
    public string? BirthDate { get; set; }
    public string? MaritalStatus { get; set; }
    public string? PatientStatus { get; set; }
    public string? Deceased { get; set; }
    public string? UsCoreSex { get; set; }
    public string? Race { get; set; }
    public string? Ethnicity { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? HomePhone { get; set; }
    public string? MobilePhone { get; set; }
    public string? Email { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? GeneralPractitioner { get; set; }
    public string? ManagingOrganization { get; set; }

    // Set once, at first insert, and never touched again — distinct from UpdatedAtUtc, which moves every re-fetch.
    // Nullable for the same reason as ResourceId/Payload above.
    public DateTime? RecordCreatedOn { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

// Permanently links a HealthApp account to the one FHIR patient its first successful launch resolved to — the
// same "account linking" concept FHIRBridge's own UserFhirContextBindings table enforces platform-wide, but
// re-homed here specifically because THIS app has ground-truth knowledge of which account is logged in (its own
// session), whereas FHIRBridge often doesn't (a genuinely embedded EHR-launch iframe can't reliably read a
// session cookie at all — see Program.cs's /api/provider-in-app-launch-context remarks). This is a UX-layer
// check on top of FHIRBridge's own gate, not a replacement for it: by the time this table can check anything,
// FHIRBridge has already run the workflow and fetched the data — it can only decide whether to show an error
// instead of the result, not prevent the underlying fetch.
//
// AudienceType is "PatientStandalone" or "EhrLaunch" (Provider EHR Launch) — enforcement differs between them:
// - PatientStandalone: strict symmetric 1:1. One account can only ever be linked to one patient, AND one patient
//   can only ever be claimed by one account — both directions checked.
// - EhrLaunch: reverse-only. A provider is expected to launch into many different patients' charts over time
//   (that's normal, not a violation), so the SAME account linking to many DIFFERENT patients is allowed. What's
//   restricted is a DIFFERENT account later claiming a patient this account already linked.
// Provider Standalone is deliberately not covered here: its context is the logged-in Practitioner (from Epic's
// id_token fhirUser claim), which FHIRBridge never surfaces to the launching app at all today — there's no
// resource id available on this side to link against without a FHIRBridge API change.
public sealed class AccountContextLinkEntity
{
    public int Id { get; set; }
    public string AccountEmail { get; set; } = string.Empty;
    public string AudienceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}

public sealed class WorkflowSettingsEntity
{
    public int Id { get; set; }
    public string WorkflowUrl { get; set; } = string.Empty;

    // Patient Standalone's own FHIRBridge connection points — previously a gitignored, per-developer local file
    // (Demo_TestApp/frontend's standalone-launch.config.ts); moved here so they're admin-configurable through the
    // same "Workflow Settings" panel as WorkflowUrl above, with no frontend rebuild needed to change them.
    // PatientWorkflowId drives the patient list fetch; PatientDetailWorkflowId is a second, independent workflow
    // used only for the per-patient detail fetch (clicking a row in the fetched list) — each needs its own
    // public-launch opt-in on the FHIRBridge side, so they're never the same id.
    public string PatientWorkflowId { get; set; } = string.Empty;
    public string PatientDetailWorkflowId { get; set; } = string.Empty;
    public string PatientBaseUrl { get; set; } = string.Empty;

    // The two workflows behind Patient_Standalone's "Download Patient Information" / "Email Patient Information"
    // buttons (see launch-standalone-patient.ts's downloadPatientInformation/emailPatientInformation) — previously
    // hardcoded in the frontend's standalone-launch.config.ts, moved here for the same reason as PatientWorkflowId
    // above. Each has its own independent public-launch opt-in on the FHIRBridge side and their CSV destinations
    // use different delivery methods (Download-URL vs Email), so they're never the same id.
    public string PatientCsvExportWorkflowId { get; set; } = string.Empty;
    public string PatientCsvEmailExportWorkflowId { get; set; } = string.Empty;

    // athenahealth variant of the Patient_Standalone list/connect flow (see launch-standalone-patient.ts's vendor
    // toggle) — a separate workflow id/base URL pair, never the same values as PatientWorkflowId/PatientBaseUrl
    // above, since athenahealth requires its own SourceConnection (different vendor, ah-practice/PracticeId, scope
    // shape). Deliberately does NOT get its own detail/CSV-export/CSV-email-export ids: this demo only exercises
    // athenahealth through the core connect-and-fetch step, not the secondary per-patient detail/export actions,
    // which stay Epic-only via PatientDetailWorkflowId/PatientCsvExportWorkflowId/PatientCsvEmailExportWorkflowId.
    // AthenaEhrEndpointId is required because FHIRBridge's public-patient-standalone-url endpoint always requires a
    // known EhrEndpoint row (type MyChart) to mint against — athenahealth's sandbox has one fixed FHIR base URL
    // (no per-hospital directory the way Epic/MyChart has), so there is no in-app picker for it; an admin instead
    // seeds a single MyChart-type EhrEndpoint row pointing at the athenahealth sandbox base URL and pastes its id
    // here once.
    public string AthenaPatientWorkflowId { get; set; } = string.Empty;
    public string AthenaPatientBaseUrl { get; set; } = string.Empty;
    public string AthenaEhrEndpointId { get; set; } = string.Empty;

    // eClinicalWorks (eCW) variant of the Patient_Standalone list/connect flow — same shape as the athenahealth
    // fields above (a separate workflow id/base URL pair, never PatientWorkflowId/PatientBaseUrl, since eCW
    // requires its own SourceConnection: Healow vendor, practice_code, patient-only audience). Also has no
    // detail/CSV-export/CSV-email-export counterpart, for the same reason athenahealth doesn't. EcwEhrEndpointId is
    // required for the same reason AthenaEhrEndpointId is: eCW's Patient audience currently targets one fixed
    // practice (no per-hospital directory), so instead of a picker, an admin pastes the id of a pre-seeded
    // EhrEndpoint row (type MyChart) whose FHIR base URL is the eCW practice's endpoint.
    public string EcwPatientWorkflowId { get; set; } = string.Empty;
    public string EcwPatientBaseUrl { get; set; } = string.Empty;
    public string EcwEhrEndpointId { get; set; } = string.Empty;

    // The two FHIRBridge workflow ids Provider_Standalone's launch-standalone-provider screen needs — "Fetch
    // Patient List" and "Patient Detail" are deliberately separate workflows (see
    // launch-standalone-provider.ts's fetchPatientList/viewPatientDetail), so each gets its own settable id here
    // rather than reusing WorkflowUrl, which is an unrelated, single opaque webhook URL for the Patient_Standalone
    // demo type.
    public string StandaloneWorkflowId { get; set; } = string.Empty;
    public string StandaloneDetailWorkflowId { get; set; } = string.Empty;
    // Provider_Standalone's own FHIRBridge connection point — previously the frontend's hardcoded
    // environment.fhirbridgeBase (a build-time constant), which only ever worked when the browser and the
    // FHIRBridge Api happened to share a host (e.g. both localhost in local dev). Moved here for the same reason
    // as PatientBaseUrl above: this demo type is meant to be admin-configurable and rebuild-free per environment.
    public string StandaloneBaseUrl { get; set; } = string.Empty;

    // Provider_InApp's single FHIRBridge EHR-launch workflow id — previously a gitignored, per-developer local
    // file (Demo_TestApp/frontend's demo-type-2/core/config/launch.config.ts), then a hand-pasted, pre-minted
    // launch-context token (a mistake-prone step: an admin pasting the raw workflow id here instead of a minted
    // token was indistinguishable at a glance and caused a full "invalid or has been tampered with" investigation).
    // Now a raw workflow id, same shape as StandaloneWorkflowId etc. above — the actual opaque launch-context token
    // is minted on demand from this id via FHIRBridge's anonymous GET /api/v1/workflows/{id}/public-launch-context
    // (see Program.cs's /api/provider-in-app-launch-context), which requires the workflow to be opted into public
    // launch via POST /api/v1/workflows/{id}/enable-public-launch. Unlike Provider_Standalone, this demo type needs
    // only one workflow: it drives a single EHR-launch exchange (FHIRBridge's /api/v1/oauth/launch/{context}), not a
    // separate list/detail pair. Provider_InApp's FHIRBridge base URL deliberately reuses StandaloneBaseUrl above
    // rather than getting its own field — both demo types are Provider-role launches against the same deployment.
    public string ProviderInAppWorkflowId { get; set; } = string.Empty;

    // BackendSystem role's "Import Practitioner" flow (see BackendSystemEndpoints.cs's
    // /api/backend-system/practitioners/import) — the FHIRBridge workflow whose Practitioner source is run, scoped
    // to the practitioner ids the user submits (patientSearchCriteria=_id=<ids>). Its FHIRBridge base URL reuses
    // StandaloneBaseUrl (same deployment), so this is just the workflow id. Seeded to the demo workflow id and
    // admin-editable via the Workflow Settings panel; defaulted here (not string.Empty) so a brand-new database is
    // immediately usable without any setup.
    public string BackendSystemPractitionerImportWorkflowId { get; set; } = "17c81a2c-b266-4ed3-9afb-8fc54910f577";
}

public sealed class HealthAppDbContext : DbContext
{
    private readonly IConfiguration _configuration;

    // IConfiguration is resolved via DI alongside DbContextOptions -- AddDbContext<T> supports any
    // additional constructor parameter the app's service provider can already resolve, and
    // IConfiguration is always registered by WebApplicationBuilder.
    public HealthAppDbContext(DbContextOptions<HealthAppDbContext> options, IConfiguration configuration) : base(options)
    {
        _configuration = configuration;
    }

    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<AccountContextLinkEntity> AccountContextLinks => Set<AccountContextLinkEntity>();

    public DbSet<HospitalEntity> Hospitals => Set<HospitalEntity>();

    public DbSet<PatientEntity> Patients => Set<PatientEntity>();

    public DbSet<WorkflowSettingsEntity> WorkflowSettings => Set<WorkflowSettingsEntity>();

    // BackendSystem role's read-only clinical tables — pre-existing HealthAppDb tables (already populated
    // outside this app's own ingestion path), mapped here purely for querying. See BackendSystemEndpoints.cs.
    public DbSet<PatientNewMappedEntity> BackendSystemPatients => Set<PatientNewMappedEntity>();

    public DbSet<PractitionerEntity> Practitioners => Set<PractitionerEntity>();

    public DbSet<EncounterEntity> Encounters => Set<EncounterEntity>();

    public DbSet<AllergyIntoleranceEntity> AllergyIntolerances => Set<AllergyIntoleranceEntity>();

    public DbSet<ObservationEntity> Observations => Set<ObservationEntity>();

    public DbSet<ConditionEntity> Conditions => Set<ConditionEntity>();

    public DbSet<ProcedureEntity> Procedures => Set<ProcedureEntity>();

    public DbSet<ServiceRequestEntity> ServiceRequests => Set<ServiceRequestEntity>();

    public DbSet<DiagnosticReportEntity> DiagnosticReports => Set<DiagnosticReportEntity>();

    public DbSet<MedicationRequestEntity> MedicationRequests => Set<MedicationRequestEntity>();

    public DbSet<MedicationAdministrationEntity> MedicationAdministrations => Set<MedicationAdministrationEntity>();

    // "_11" curated landing tables (Patient_11 .. Procedure_11) — the business/layman view read by the "New 11"
    // menu each non-Admin role gets. Read-only; rows are loaded externally (or by EnsureCreated on a brand-new
    // database). See Resource11Entities.cs and Resource11Endpoints.cs.
    public DbSet<Patient11Entity> Patients11 => Set<Patient11Entity>();

    public DbSet<Practitioner11Entity> Practitioners11 => Set<Practitioner11Entity>();

    public DbSet<Encounter11Entity> Encounters11 => Set<Encounter11Entity>();

    public DbSet<Observation11Entity> Observations11 => Set<Observation11Entity>();

    public DbSet<Condition11Entity> Conditions11 => Set<Condition11Entity>();

    public DbSet<AllergyIntolerance11Entity> AllergyIntolerances11 => Set<AllergyIntolerance11Entity>();

    public DbSet<MedicationRequest11Entity> MedicationRequests11 => Set<MedicationRequest11Entity>();

    public DbSet<MedicationAdministration11Entity> MedicationAdministrations11 => Set<MedicationAdministration11Entity>();

    public DbSet<ServiceRequest11Entity> ServiceRequests11 => Set<ServiceRequest11Entity>();

    public DbSet<DiagnosticReport11Entity> DiagnosticReports11 => Set<DiagnosticReport11Entity>();

    public DbSet<Procedure11Entity> Procedures11 => Set<Procedure11Entity>();

    // "New 11" workflow URLs (single row, List + Details per role). Ensured + seeded at startup (see Program.cs)
    // rather than via HasData, so it works against an already-existing HealthAppDb too.
    public DbSet<Resource11WorkflowSettingsEntity> Resource11WorkflowSettings => Set<Resource11WorkflowSettingsEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Table/key mapping only — these 11 tables already exist in HealthAppDb (created outside EnsureCreated,
        // which never alters an already-existing database; see the comment on db.Database.EnsureCreated() in
        // Program.cs), so there is no HasData seeding here, just enough Fluent config for EF to read/write the
        // exact existing table names and string primary keys.
        modelBuilder.Entity<PatientNewMappedEntity>(e =>
        {
            e.ToTable("Patient_NewMapped");
            e.HasKey(x => x.PatientId);
        });
        modelBuilder.Entity<PractitionerEntity>(e =>
        {
            e.ToTable("Practitioner");
            e.HasKey(x => x.PractitionerId);
        });
        modelBuilder.Entity<EncounterEntity>(e =>
        {
            e.ToTable("Encounter");
            e.HasKey(x => x.EncounterId);
        });
        modelBuilder.Entity<AllergyIntoleranceEntity>(e =>
        {
            e.ToTable("AllergyIntolerance");
            e.HasKey(x => x.AllergyIntoleranceId);
        });
        modelBuilder.Entity<ObservationEntity>(e =>
        {
            e.ToTable("Observation_NewMapped");
            e.HasKey(x => x.ObservationId);
        });
        modelBuilder.Entity<ConditionEntity>(e =>
        {
            e.ToTable("Condition_NewMapped");
            e.HasKey(x => x.ConditionId);
        });
        modelBuilder.Entity<ProcedureEntity>(e =>
        {
            e.ToTable("Procedure_NewMapped");
            e.HasKey(x => x.ProcedureId);
        });
        modelBuilder.Entity<ServiceRequestEntity>(e =>
        {
            e.ToTable("ServiceRequest");
            e.HasKey(x => x.ServiceRequestId);
        });
        modelBuilder.Entity<DiagnosticReportEntity>(e =>
        {
            e.ToTable("DiagnosticReport");
            e.HasKey(x => x.DiagnosticReportId);
        });
        modelBuilder.Entity<MedicationRequestEntity>(e =>
        {
            e.ToTable("MedicationRequest");
            e.HasKey(x => x.MedicationRequestId);
        });
        modelBuilder.Entity<MedicationAdministrationEntity>(e =>
        {
            e.ToTable("MedicationAdministration");
            e.HasKey(x => x.MedicationAdministrationId);
        });

        // "_11" curated tables. On a brand-new database EnsureCreated builds these from the entity model; on an
        // existing HealthAppDb they must be created out-of-band by "9 resource tables _11 (curated).sql" (same
        // caveat as the tables above — EnsureCreated never alters an already-existing database).
        modelBuilder.Entity<Patient11Entity>(e =>
        {
            e.ToTable("Patient_11");
            e.HasKey(x => x.PatientId);
        });
        modelBuilder.Entity<Practitioner11Entity>(e =>
        {
            e.ToTable("Practitioner_11");
            e.HasKey(x => x.PractitionerId);
        });
        modelBuilder.Entity<Encounter11Entity>(e =>
        {
            e.ToTable("Encounter_11");
            e.HasKey(x => x.EncounterId);
        });
        modelBuilder.Entity<Observation11Entity>(e =>
        {
            e.ToTable("Observation_11");
            e.HasKey(x => x.ObservationId);
        });
        modelBuilder.Entity<Condition11Entity>(e =>
        {
            e.ToTable("Condition_11");
            e.HasKey(x => x.ConditionId);
        });
        modelBuilder.Entity<AllergyIntolerance11Entity>(e =>
        {
            e.ToTable("AllergyIntolerance_11");
            e.HasKey(x => x.AllergyId);
        });
        modelBuilder.Entity<MedicationRequest11Entity>(e =>
        {
            e.ToTable("MedicationRequest_11");
            e.HasKey(x => x.MedicationRequestId);
        });
        modelBuilder.Entity<MedicationAdministration11Entity>(e =>
        {
            e.ToTable("MedicationAdministration_11");
            e.HasKey(x => x.MedicationAdministrationId);
        });
        modelBuilder.Entity<ServiceRequest11Entity>(e =>
        {
            e.ToTable("ServiceRequest_11");
            e.HasKey(x => x.ServiceRequestId);
        });
        modelBuilder.Entity<DiagnosticReport11Entity>(e =>
        {
            e.ToTable("DiagnosticReport_11");
            e.HasKey(x => x.DiagnosticReportId);
        });
        modelBuilder.Entity<Procedure11Entity>(e =>
        {
            e.ToTable("Procedure_11");
            e.HasKey(x => x.ProcedureId);
        });
        modelBuilder.Entity<Resource11WorkflowSettingsEntity>(e =>
        {
            e.ToTable("Resource11WorkflowSettings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PatientListWorkflowUrl).HasColumnName("Patient_List_11").HasMaxLength(1000);
            e.Property(x => x.PatientDetailsWorkflowUrl).HasColumnName("Patient_Details_11").HasMaxLength(1000);
            e.Property(x => x.ProviderListWorkflowUrl).HasColumnName("Provider_List_11").HasMaxLength(1000);
            e.Property(x => x.ProviderDetailsWorkflowUrl).HasColumnName("Provider_Details_11").HasMaxLength(1000);
            e.Property(x => x.ProviderInAppListWorkflowUrl).HasColumnName("ProviderInApp_List_11").HasMaxLength(1000);
            e.Property(x => x.ProviderInAppDetailsWorkflowUrl).HasColumnName("ProviderInApp_Details_11").HasMaxLength(1000);
            e.Property(x => x.BackendSystemListWorkflowUrl).HasColumnName("BackendSystem_List_11").HasMaxLength(1000);
            e.Property(x => x.BackendSystemDetailsWorkflowUrl).HasColumnName("BackendSystem_Details_11").HasMaxLength(1000);
        });

        // DB-level default so RecordCreatedOn is populated even for rows a real destination writer inserts
        // directly (bypassing this app's own /api/workflow/run, which sets it explicitly on insert).
        modelBuilder.Entity<PatientEntity>()
            .Property(p => p.RecordCreatedOn)
            .HasDefaultValueSql("SYSUTCDATETIME()");

        modelBuilder.Entity<UserEntity>().HasData(
            new UserEntity
            {
                Id = 1,
                Email = "admin@healthapp.local",
                Password = "Admin@123",
                Role = UserRoles.Admin
            },
            new UserEntity
            {
                Id = 2,
                Email = "patient@healthapp.local",
                Password = "Patient@123",
                Role = UserRoles.Patient
            },
            new UserEntity
            {
                Id = 3,
                Email = "providerstandalone@healthapp.local",
                Password = "Provider@123",
                Role = UserRoles.ProviderStandalone
            },
            new UserEntity
            {
                Id = 5,
                Email = "providerInApp@healthapp.local",
                Password = "Provider@123",
                Role = UserRoles.ProviderInApp
            },
            new UserEntity
            {
                Id = 6,
                Email = "backendsystem@healthapp.local",
                Password = "Backend@123",
                Role = UserRoles.BackendSystem
            });

        modelBuilder.Entity<HospitalEntity>().HasData(
            new HospitalEntity { Id = 1, Name = "St. Mary's Medical Center", OrganizationId = 4521 },
            new HospitalEntity { Id = 2, Name = "Riverside General Hospital", OrganizationId = 3287 },
            new HospitalEntity { Id = 3, Name = "Lakeview Community Health", OrganizationId = 9012 },
            new HospitalEntity { Id = 4, Name = "Mercy Regional Medical Center", OrganizationId = 6740 },
            new HospitalEntity { Id = 5, Name = "Cedar Grove Hospital", OrganizationId = 1183 });

        var seedTimestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seedFields = PatientFieldExtractor.Extract(PatientSeedData.CamilaLopezJson);

        modelBuilder.Entity<PatientEntity>().HasData(new PatientEntity
        {
            Id = 1,
            ResourceType = "Patient",
            ResourceId = "erXuFYUfucBZaryVksYEcMg3",
            Payload = PatientSeedData.CamilaLopezJson,
            FullName = seedFields.FullName,
            FirstName = seedFields.FirstName,
            MiddleName = seedFields.MiddleName,
            LastName = seedFields.LastName,
            Gender = seedFields.Gender,
            LegalSex = seedFields.LegalSex,
            SexForClinicalUse = seedFields.SexForClinicalUse,
            Pronouns = seedFields.Pronouns,
            BirthDate = seedFields.BirthDate,
            MaritalStatus = seedFields.MaritalStatus,
            PatientStatus = seedFields.PatientStatus,
            Deceased = seedFields.Deceased,
            UsCoreSex = seedFields.UsCoreSex,
            Race = seedFields.Race,
            Ethnicity = seedFields.Ethnicity,
            Address = seedFields.Address,
            City = seedFields.City,
            State = seedFields.State,
            PostalCode = seedFields.PostalCode,
            Country = seedFields.Country,
            HomePhone = seedFields.HomePhone,
            MobilePhone = seedFields.MobilePhone,
            Email = seedFields.Email,
            PreferredLanguage = seedFields.PreferredLanguage,
            GeneralPractitioner = seedFields.GeneralPractitioner,
            ManagingOrganization = seedFields.ManagingOrganization,
            RecordCreatedOn = seedTimestamp,
            UpdatedAtUtc = seedTimestamp
        });

        modelBuilder.Entity<WorkflowSettingsEntity>().HasData(new WorkflowSettingsEntity
        {
            Id = 1,
            WorkflowUrl = string.Empty,
            PatientWorkflowId = string.Empty,
            PatientDetailWorkflowId = string.Empty,
            // Seeded once, only for a brand-new database (see EnsureCreated() in Program.cs) -- admin-editable
            // afterward via the Workflow Settings panel, same as every other field in this row. Sourced from
            // config (DefaultWorkflowSettings:PatientBaseUrl) rather than hardcoded, so each environment's own
            // appsettings.Production.json can seed a sensible default matching that environment's own FHIRBridge
            // Api instead of every environment seeding the same placeholder.
            PatientBaseUrl = _configuration["DefaultWorkflowSettings:PatientBaseUrl"] ?? string.Empty,
            // Seeded with the ids that were previously hardcoded in the frontend's standalone-launch.config.ts, so
            // migrating a fresh database preserves today's "Download"/"Email Patient Information" behavior until an
            // admin overrides them via the Workflow Settings panel.
            PatientCsvExportWorkflowId = "a0de009e-9a60-494f-9ff8-d83cefdd1a3b",
            PatientCsvEmailExportWorkflowId = "c5e813f5-04fe-4223-8465-fba1a1e83b75",
            AthenaPatientWorkflowId = string.Empty,
            AthenaPatientBaseUrl = string.Empty,
            AthenaEhrEndpointId = string.Empty,
            EcwPatientWorkflowId = string.Empty,
            EcwPatientBaseUrl = string.Empty,
            EcwEhrEndpointId = string.Empty,
            StandaloneWorkflowId = string.Empty,
            StandaloneDetailWorkflowId = string.Empty,
            // Same sourcing rationale as PatientBaseUrl above (DefaultWorkflowSettings:StandaloneBaseUrl).
            StandaloneBaseUrl = _configuration["DefaultWorkflowSettings:StandaloneBaseUrl"] ?? string.Empty,
            BackendSystemPractitionerImportWorkflowId = "17c81a2c-b266-4ed3-9afb-8fc54910f577"
        });
    }
}
