using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HealthAppBackend;

public static class UserRoles
{
    public const string Admin = "Admin";
    public const string Patient = "Patient";
    public const string ProviderStandalone = "ProviderStandalone";
    public const string ProviderInApp = "ProviderInApp";
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

    // The two FHIRBridge workflow ids Provider_Standalone's launch-standalone-provider screen needs — "Fetch
    // Patient List" and "Patient Detail" are deliberately separate workflows (see
    // launch-standalone-provider.ts's fetchPatientList/viewPatientDetail), so each gets its own settable id here
    // rather than reusing WorkflowUrl, which is an unrelated, single opaque webhook URL for the Patient_Standalone
    // demo type.
    public string StandaloneWorkflowId { get; set; } = string.Empty;
    public string StandaloneDetailWorkflowId { get; set; } = string.Empty;

    // Provider_InApp's single FHIRBridge launch-context token — previously a gitignored, per-developer local file
    // (Demo_TestApp/frontend's demo-type-2/core/config/launch.config.ts); moved here so it's admin-configurable
    // through its own Settings gear (see launch-provider-in-app.ts) with no frontend rebuild needed to change it.
    // Unlike Provider_Standalone, this demo type needs only one token: it drives a single EHR-launch exchange
    // (FHIRBridge's /api/v1/oauth/launch/{context}), not a separate list/detail workflow pair.
    public string ProviderLaunchContext { get; set; } = string.Empty;
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

    public DbSet<HospitalEntity> Hospitals => Set<HospitalEntity>();

    public DbSet<PatientEntity> Patients => Set<PatientEntity>();

    public DbSet<WorkflowSettingsEntity> WorkflowSettings => Set<WorkflowSettingsEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            StandaloneWorkflowId = string.Empty,
            StandaloneDetailWorkflowId = string.Empty
        });
    }
}
