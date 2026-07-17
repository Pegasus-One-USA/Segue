using Microsoft.EntityFrameworkCore;

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

// Drives the "Login Type" dropdown on the full-screen login page. The selected name determines which
// component/UI the app loads post-login (e.g. "Patient_Standalone" loads the existing mobile view).
public sealed class DemoTypeEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
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

    // The two FHIRBridge workflow ids Provider_Standalone's launch-standalone-provider screen needs — "Fetch
    // Patient List" and "Patient Detail" are deliberately separate workflows (see
    // launch-standalone-provider.ts's fetchPatientList/viewPatientDetail), so each gets its own settable id here
    // rather than reusing WorkflowUrl, which is an unrelated, single opaque webhook URL for the Patient_Standalone
    // demo type.
    public string StandaloneWorkflowId { get; set; } = string.Empty;
    public string StandaloneDetailWorkflowId { get; set; } = string.Empty;
}

public sealed class HealthAppDbContext : DbContext
{
    public HealthAppDbContext(DbContextOptions<HealthAppDbContext> options) : base(options)
    {
    }

    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<HospitalEntity> Hospitals => Set<HospitalEntity>();

    public DbSet<DemoTypeEntity> DemoTypes => Set<DemoTypeEntity>();

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

        modelBuilder.Entity<DemoTypeEntity>().ToTable("DemoType");
        modelBuilder.Entity<DemoTypeEntity>().HasData(
            new DemoTypeEntity { Id = 1, Name = "Patient_Standalone" },
            new DemoTypeEntity { Id = 2, Name = "Provider_Standalone" },
            new DemoTypeEntity { Id = 3, Name = "Provider_InApp" });

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
            PatientBaseUrl = "http://localhost:5000",
            StandaloneWorkflowId = string.Empty,
            StandaloneDetailWorkflowId = string.Empty
        });
    }
}
