using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>What kind of stored credential (if any) a HAPI terminology sync needs before it can run.
/// LoincBasicAuth and UtsApiKey point at the exact same ProvisionedSecrets the legacy per-system
/// Terminology tabs (LoincConfigurationService/SnomedConfigurationService/RxNormConfigurationService)
/// already write — entering a credential via either surface satisfies both.</summary>
public enum HapiCredentialKind
{
    None,
    LoincBasicAuth,
    UtsApiKey,
}

/// <summary>Normalized result of one sync run, since the 13 concrete Hapi*SyncResult records differ
/// slightly in shape (only Loinc/Snomed/RxNorm/Icd10 carry a version/release string).</summary>
public sealed record HapiSyncOutcome(int Count, string? Version, TimeSpan Duration);

public sealed record HapiTerminologySystemDescriptor(
    string Code,
    string DisplayName,
    HapiCredentialKind CredentialKind,
    Func<IServiceProvider, CancellationToken, Task<HapiSyncOutcome>> RunAsync,
    /// <summary>Set only for the 6 systems (Snomed, RxNorm, Loinc, Icd10Pcs, Hcpcs, Mesh) whose sync
    /// service implements <see cref="IHapiVersionCheckable"/> — lets a "scan for new version" check
    /// query the source's latest available version without downloading/importing anything. Null for
    /// the other 7 systems (Cvx, Ucum, Icpc3, Dcm, Ndc, Icd10, Icd11): either their source publishes
    /// undated rolling snapshots with no discoverable "latest" pointer, or their sync uses a hardcoded
    /// source URL/year with no separate listing to check against.</summary>
    Func<IServiceProvider, CancellationToken, Task<string?>>? CheckLatestVersionAsync = null,
    /// <summary>Set only for the systems whose HAPI sync actually reads a configurable download
    /// endpoint (currently just LOINC, via the shared ILoincReleaseClient) — the legacy per-system
    /// setting key to read/write, e.g. "Terminology:Loinc:DownloadApiUrl". Everything else the legacy
    /// group holds (SchedulerEnabled/Frequency/ExecutionTime, FhirApiUrl, retry/timeout settings) is
    /// either superseded by this system's own *Hapi:* settings or unused by any sync code at all.</summary>
    string? DownloadApiUrlSettingKey = null,
    /// <summary>The canonical FHIR CodeSystem URI this system's sync writes into
    /// <see cref="FHIRBridge.Domain.Entities.Terminology.TrmCodeSystem.CodeSystemUri"/> (e.g.
    /// "http://hl7.org/fhir/sid/icd-10-cm" for ICD-10-CM) — duplicated here from each private
    /// Hapi*TerminologySyncService.SystemUrl constant so TerminologyConceptService can resolve a
    /// short registry code to its local-storage row without depending on all 13 sync services.</summary>
    string CodeSystemUri = "")
{
    public string SettingsKeyPrefix => $"Terminology:{Code}Hapi";
    public static readonly IReadOnlyList<string> FrequencyOptions = new[] { "Weekly", "Monthly" };
}

/// <summary>
/// Single source of truth for the 13 HAPI-terminology-server sync systems — display name, credential
/// requirement, and how to actually run one (resolving the matching IHapi{Code}TerminologySyncService and
/// normalizing its result). Used by HapiTerminologyConfigurationService for both settings and Run Now.
/// </summary>
public sealed class HapiTerminologySystemRegistry
{
    private readonly IReadOnlyDictionary<string, HapiTerminologySystemDescriptor> _byCode;

    public HapiTerminologySystemRegistry()
    {
        var all = new List<HapiTerminologySystemDescriptor>
        {
            new("Cvx", "CVX", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiCvxTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, null, r.Duration);
            }, CodeSystemUri: "http://hl7.org/fhir/sid/cvx"),
            new("Dcm", "DCM", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiDcmTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, null, r.Duration);
            }, CodeSystemUri: "http://dicom.nema.org/resources/ontology/DCM"),
            new("Hcpcs", "HCPCS", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiHcpcsTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, r.Version, r.Duration);
            },
            CheckLatestVersionAsync: (sp, ct) =>
                sp.GetRequiredService<IHapiHcpcsTerminologySyncService>() is IHapiVersionCheckable c
                    ? c.GetLatestAvailableVersionAsync(ct)
                    : Task.FromResult<string?>(null),
            CodeSystemUri: "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets"),
            new("Icd10", "ICD-10-CM", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiIcd10TerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, r.ReleaseYear, r.Duration);
            }, CodeSystemUri: "http://hl7.org/fhir/sid/icd-10-cm"),
            new("Icd10Pcs", "ICD-10-PCS", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiIcd10PcsTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, r.Version, r.Duration);
            },
            CheckLatestVersionAsync: (sp, ct) =>
                sp.GetRequiredService<IHapiIcd10PcsTerminologySyncService>() is IHapiVersionCheckable c
                    ? c.GetLatestAvailableVersionAsync(ct)
                    : Task.FromResult<string?>(null),
            CodeSystemUri: "http://www.cms.gov/Medicare/Coding/ICD10"),
            new("Icd11", "ICD-11 MMS", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiIcd11TerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, null, r.Duration);
            }, CodeSystemUri: "http://id.who.int/icd/release/11/mms"),
            new("Icpc3", "ICPC-3", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiIcpc3TerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, null, r.Duration);
            }, CodeSystemUri: "http://terminology.hl7.org/CodeSystem/ICPC-3"),
            new("Loinc", "LOINC", HapiCredentialKind.LoincBasicAuth, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiLoincTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, r.Version, r.Duration);
            },
            CheckLatestVersionAsync: (sp, ct) =>
                sp.GetRequiredService<IHapiLoincTerminologySyncService>() is IHapiVersionCheckable c
                    ? c.GetLatestAvailableVersionAsync(ct)
                    : Task.FromResult<string?>(null),
            DownloadApiUrlSettingKey: "Terminology:Loinc:DownloadApiUrl", CodeSystemUri: "http://loinc.org"),
            new("Mesh", "MeSH", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiMeshTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, r.Version, r.Duration);
            },
            CheckLatestVersionAsync: (sp, ct) =>
                sp.GetRequiredService<IHapiMeshTerminologySyncService>() is IHapiVersionCheckable c
                    ? c.GetLatestAvailableVersionAsync(ct)
                    : Task.FromResult<string?>(null),
            CodeSystemUri: "https://www.nlm.nih.gov/mesh"),
            new("Ndc", "NDC", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiNdcTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, null, r.Duration);
            }, CodeSystemUri: "http://hl7.org/fhir/sid/ndc"),
            new("RxNorm", "RxNorm", HapiCredentialKind.UtsApiKey, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiRxNormTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, r.Version, r.Duration);
            },
            CheckLatestVersionAsync: (sp, ct) =>
                sp.GetRequiredService<IHapiRxNormTerminologySyncService>() is IHapiVersionCheckable c
                    ? c.GetLatestAvailableVersionAsync(ct)
                    : Task.FromResult<string?>(null),
            CodeSystemUri: "http://www.nlm.nih.gov/research/umls/rxnorm"),
            new("Snomed", "SNOMED CT", HapiCredentialKind.UtsApiKey, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiSnomedTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, r.Version, r.Duration);
            },
            CheckLatestVersionAsync: (sp, ct) =>
                sp.GetRequiredService<IHapiSnomedTerminologySyncService>() is IHapiVersionCheckable c
                    ? c.GetLatestAvailableVersionAsync(ct)
                    : Task.FromResult<string?>(null),
            CodeSystemUri: "http://snomed.info/sct"),
            new("Ucum", "UCUM", HapiCredentialKind.None, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IHapiUcumTerminologySyncService>().SyncAsync(ct);
                return new HapiSyncOutcome(r.TotalConceptCount, null, r.Duration);
            }, CodeSystemUri: "http://unitsofmeasure.org"),
        };

        _byCode = all.ToDictionary(x => x.Code, x => x, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<HapiTerminologySystemDescriptor> All => _byCode.Values.ToList();

    public HapiTerminologySystemDescriptor? TryGet(string code) => _byCode.GetValueOrDefault(code);

    public HapiTerminologySystemDescriptor Get(string code) =>
        TryGet(code) ?? throw new InvalidOperationException($"Unknown terminology code system '{code}'.");
}
