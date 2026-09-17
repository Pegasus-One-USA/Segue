using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Services.Transforms;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// See <see cref="ISystemSettingsSeeder"/>. Every default below is read from IConfiguration first — so a
/// deployment whose appsettings already overrides a key (e.g. GeneratedFileDownload:PublicBaseUrl set to
/// the real VM hostname) seeds that same value, not the compiled-in placeholder — falling back to the
/// literal only when the section is genuinely absent (matching each consumer's own fallback).
/// </summary>
public sealed class SystemSettingsSeeder : ISystemSettingsSeeder
{
    private readonly ISystemSettingRepository _repository;
    private readonly IConfiguration _configuration;

    public SystemSettingsSeeder(ISystemSettingRepository repository, IConfiguration configuration)
    {
        _repository = repository;
        _configuration = configuration;
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        var defaults = new (string Key, string Value, string Description)[]
        {
            (
                "GeneratedFileDownload:PublicBaseUrl",
                _configuration.GetValue("GeneratedFileDownload:PublicBaseUrl", "http://localhost:5000"),
                "Base URL used to build generated-file download links returned to callers."),

            ("RuntimeWorker:Enabled", Bool("RuntimeWorker:Enabled", true), "Master on/off for the scheduled Phase 1 runtime worker."),
            ("RuntimeWorker:IntervalSeconds", Int("RuntimeWorker:IntervalSeconds", 300), "Runtime worker poll interval, in seconds."),
            ("ScheduleDispatcher:Enabled", Bool("ScheduleDispatcher:Enabled", true), "Master on/off for the queue-based schedule dispatcher."),
            ("ScheduleDispatcher:IntervalSeconds", Int("ScheduleDispatcher:IntervalSeconds", 60), "Schedule dispatcher poll interval, in seconds."),
            ("ScheduleDispatcher:HeartbeatLoggingEnabled", Bool("ScheduleDispatcher:HeartbeatLoggingEnabled", true), "Master on/off for the schedule dispatcher's verbose per-tick heartbeat and per-route due/skip diagnostic logs. Turn off once scheduling is confirmed healthy to cut log volume."),
            ("EndpointHealthCheck:Enabled", Bool("EndpointHealthCheck:Enabled", false), "Master on/off for periodic source/destination connectivity checks."),
            ("EndpointHealthCheck:IntervalSeconds", Int("EndpointHealthCheck:IntervalSeconds", 300), "Endpoint health check interval, in seconds."),
            ("AuditChainVerification:Enabled", Bool("AuditChainVerification:Enabled", true), "Master on/off for the scheduled audit-log hash-chain verification."),
            ("AuditChainVerification:IntervalHours", Int("AuditChainVerification:IntervalHours", 24), "Audit chain verification interval, in hours."),
            ("AlertEvaluation:Enabled", Bool("AlertEvaluation:Enabled", true), "Master on/off for the alert-rule evaluation worker."),
            ("AlertEvaluation:IntervalSeconds", Int("AlertEvaluation:IntervalSeconds", 300), "Alert evaluation interval, in seconds."),

            // Workflow Numbering — surfaces as one "Workflow Numbering" group row on Settings > System
            // Settings > General (grouping is automatic: generalSettingGroupOf() splits on the key prefix).
            // Policy only; the live per-period counter lives in WorkflowNumberSequences, not here.
            ("WorkflowNumbering:Enabled", Bool("WorkflowNumbering:Enabled", true), "Master on/off for generated workflow numbers (e.g. WLW-150926-0042). When off, new workflows are created without a number."),
            ("WorkflowNumbering:Prefix", _configuration.GetValue("WorkflowNumbering:Prefix", "WLW"), "Leading segment of a generated workflow number."),
            ("WorkflowNumbering:DateFormat", _configuration.GetValue("WorkflowNumbering:DateFormat", "ddMMyy"), "Date segment format (.NET format string), e.g. ddMMyy or yyyyMMdd."),
            ("WorkflowNumbering:ResetPolicy", _configuration.GetValue("WorkflowNumbering:ResetPolicy", "Daily"), "When the incremental counter restarts at 1: Never, Daily, Monthly, Quarterly, Yearly, or CustomAnchorDate."),
            ("WorkflowNumbering:AnchorDate", _configuration.GetValue("WorkflowNumbering:AnchorDate", "04-01"), "Anniversary (MM-dd) the counter restarts on when ResetPolicy is CustomAnchorDate. Ignored otherwise."),
            ("WorkflowNumbering:PadWidth", Int("WorkflowNumbering:PadWidth", 4), "Digits in the incremental segment, zero-padded (4 gives 0001)."),

            ("AnomalyDetection:LatencySigmaMultiplier", Double("AnomalyDetection:LatencySigmaMultiplier", 3.0), "Standard-deviation multiplier that flags a latency outlier."),
            ("AnomalyDetection:MinBaselineRuns", Int("AnomalyDetection:MinBaselineRuns", 5), "Minimum runs required before a statistical baseline is trusted."),
            ("AnomalyDetection:ThroughputDropFraction", Double("AnomalyDetection:ThroughputDropFraction", 0.2), "Fraction of baseline extraction below which a throughput drop is flagged."),
            ("AnomalyDetection:ErrorRateWarnThreshold", Int("AnomalyDetection:ErrorRateWarnThreshold", 0), "Logged-error count above which an error-rate anomaly is raised."),
            ("AnomalyDetection:DetectFailures", Bool("AnomalyDetection:DetectFailures", true), "Toggle: flag failed runs."),
            ("AnomalyDetection:DetectWriteRatio", Bool("AnomalyDetection:DetectWriteRatio", true), "Toggle: flag low write ratios."),
            ("AnomalyDetection:DetectLatency", Bool("AnomalyDetection:DetectLatency", true), "Toggle: flag latency outliers."),
            ("AnomalyDetection:DetectZeroExtraction", Bool("AnomalyDetection:DetectZeroExtraction", true), "Toggle: flag zero-extraction runs."),
            ("AnomalyDetection:DetectThroughputDrop", Bool("AnomalyDetection:DetectThroughputDrop", true), "Toggle: flag throughput drops."),
            ("AnomalyDetection:DetectErrorRate", Bool("AnomalyDetection:DetectErrorRate", true), "Toggle: flag elevated error rates."),

            ("IncrementalSync:Enabled", Bool("IncrementalSync:Enabled", true), "Master on/off for automatic incremental (_lastUpdated) source extraction."),
            ("IncrementalSync:OverlapSeconds", Int("IncrementalSync:OverlapSeconds", 60), "Seconds subtracted from the incremental-sync watermark to tolerate clock skew."),
            ("Caching:TerminologyTtlMinutes", Int("Caching:TerminologyTtlMinutes", 60), "Terminology lookup/translation cache TTL, in minutes."),
            ("Terminology:Loinc:DownloadApiUrl", _configuration.GetValue("Terminology:Loinc:DownloadApiUrl", string.Empty), "Official LOINC release-download endpoint. Credentials are stored only as ProvisionedSecrets."),
            ("Terminology:Loinc:FhirApiUrl", _configuration.GetValue("Terminology:Loinc:FhirApiUrl", string.Empty), "Optional official LOINC FHIR terminology endpoint."),
            ("Terminology:Loinc:UsernameSecretName", _configuration.GetValue("Terminology:Loinc:UsernameSecretName", "loinc-basic-username"), "ProvisionedSecrets name for the LOINC download username."),
            ("Terminology:Loinc:PasswordSecretName", _configuration.GetValue("Terminology:Loinc:PasswordSecretName", "loinc-basic-password"), "ProvisionedSecrets name for the LOINC download password."),
            ("Terminology:Loinc:SchedulerEnabled", Bool("Terminology:Loinc:SchedulerEnabled", false), "Master switch for scheduled LOINC synchronization."),
            ("Terminology:Loinc:Frequency", _configuration.GetValue("Terminology:Loinc:Frequency", "Monthly"), "LOINC synchronization frequency: Weekly or Monthly."),
            ("Terminology:Loinc:ExecutionTime", _configuration.GetValue("Terminology:Loinc:ExecutionTime", "02:00"), "Local execution time for scheduled LOINC synchronization (HH:mm)."),
            ("Terminology:Loinc:RetryCount", Int("Terminology:Loinc:RetryCount", 3), "Number of LOINC synchronization retries after a transient failure."),
            ("Terminology:Loinc:RetryIntervalSeconds", Int("Terminology:Loinc:RetryIntervalSeconds", 60), "Seconds to wait between LOINC synchronization retries."),
            ("Terminology:Loinc:DownloadTimeoutSeconds", Int("Terminology:Loinc:DownloadTimeoutSeconds", 900), "Maximum time to download a LOINC release archive."),

            // Shared by every vocabulary below and by FhirTerminologyLookupService/Translation/Validation/
            // Expansion — one terminology server, one address, not a separate ServerBaseUrl per vocabulary.
            ("Terminology:BaseUrl", _configuration.GetValue("Terminology:BaseUrl", "http://hapi-terminology:8080/fhir"), "Base URL of the FHIR terminology server — used both for pipeline code lookup/validation/translation/expansion, and as the upload target for every automatic vocabulary sync below."),

            ("Terminology:Icd10Hapi:SchedulerEnabled", Bool("Terminology:Icd10Hapi:SchedulerEnabled", false), "Master switch for automatically downloading the official CMS/CDC ICD-10-CM release and loading it into the terminology server, on a schedule."),
            ("Terminology:Icd10Hapi:Frequency", _configuration.GetValue("Terminology:Icd10Hapi:Frequency", "Monthly"), "ICD-10-CM terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:Icd10Hapi:ExecutionTime", _configuration.GetValue("Terminology:Icd10Hapi:ExecutionTime", "03:00"), "Local execution time for the scheduled ICD-10-CM terminology-server sync (HH:mm)."),

            ("Terminology:CvxHapi:SchedulerEnabled", Bool("Terminology:CvxHapi:SchedulerEnabled", false), "Master switch for automatically downloading the official CDC CVX vaccine code table and loading it into the terminology server, on a schedule."),
            ("Terminology:CvxHapi:Frequency", _configuration.GetValue("Terminology:CvxHapi:Frequency", "Monthly"), "CVX terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:CvxHapi:ExecutionTime", _configuration.GetValue("Terminology:CvxHapi:ExecutionTime", "03:15"), "Local execution time for the scheduled CVX terminology-server sync (HH:mm)."),

            ("Terminology:NdcHapi:SchedulerEnabled", Bool("Terminology:NdcHapi:SchedulerEnabled", false), "Master switch for automatically downloading the official FDA NDC directory and loading it into the terminology server, on a schedule."),
            ("Terminology:NdcHapi:Frequency", _configuration.GetValue("Terminology:NdcHapi:Frequency", "Monthly"), "NDC terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:NdcHapi:ExecutionTime", _configuration.GetValue("Terminology:NdcHapi:ExecutionTime", "03:30"), "Local execution time for the scheduled NDC terminology-server sync (HH:mm)."),

            ("Terminology:HcpcsHapi:SchedulerEnabled", Bool("Terminology:HcpcsHapi:SchedulerEnabled", false), "Master switch for automatically downloading the latest official CMS HCPCS Level II quarterly release and loading it into the terminology server, on a schedule."),
            ("Terminology:HcpcsHapi:Frequency", _configuration.GetValue("Terminology:HcpcsHapi:Frequency", "Monthly"), "HCPCS terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:HcpcsHapi:ExecutionTime", _configuration.GetValue("Terminology:HcpcsHapi:ExecutionTime", "03:45"), "Local execution time for the scheduled HCPCS terminology-server sync (HH:mm)."),

            ("Terminology:UcumHapi:SchedulerEnabled", Bool("Terminology:UcumHapi:SchedulerEnabled", false), "Master switch for automatically downloading the official UCUM specification and loading it into the terminology server, on a schedule."),
            ("Terminology:UcumHapi:Frequency", _configuration.GetValue("Terminology:UcumHapi:Frequency", "Monthly"), "UCUM terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:UcumHapi:ExecutionTime", _configuration.GetValue("Terminology:UcumHapi:ExecutionTime", "04:00"), "Local execution time for the scheduled UCUM terminology-server sync (HH:mm)."),

            ("Terminology:LoincHapi:SchedulerEnabled", Bool("Terminology:LoincHapi:SchedulerEnabled", false), "Master switch for automatically downloading the credentialed LOINC release and loading it into the terminology server, on a schedule. Requires the same LOINC account already configured for Terminology:Loinc:* (loinc-basic-username/loinc-basic-password ProvisionedSecrets)."),
            ("Terminology:LoincHapi:Frequency", _configuration.GetValue("Terminology:LoincHapi:Frequency", "Monthly"), "LOINC terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:LoincHapi:ExecutionTime", _configuration.GetValue("Terminology:LoincHapi:ExecutionTime", "04:15"), "Local execution time for the scheduled LOINC terminology-server sync (HH:mm)."),

            ("Terminology:RxNormHapi:SchedulerEnabled", Bool("Terminology:RxNormHapi:SchedulerEnabled", false), "Master switch for automatically downloading the credentialed RxNorm release and loading it into the terminology server, on a schedule. Requires the same UMLS/UTS API key already configured on the RxNorm/SNOMED settings page (uts-api-key ProvisionedSecret)."),
            ("Terminology:RxNormHapi:Frequency", _configuration.GetValue("Terminology:RxNormHapi:Frequency", "Monthly"), "RxNorm terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:RxNormHapi:ExecutionTime", _configuration.GetValue("Terminology:RxNormHapi:ExecutionTime", "04:30"), "Local execution time for the scheduled RxNorm terminology-server sync (HH:mm)."),

            ("Terminology:SnomedHapi:SchedulerEnabled", Bool("Terminology:SnomedHapi:SchedulerEnabled", false), "Master switch for automatically downloading the credentialed SNOMED CT (US Edition) release and loading it into the terminology server, on a schedule. Requires the same UMLS/UTS API key already configured on the RxNorm/SNOMED settings page (uts-api-key ProvisionedSecret)."),
            ("Terminology:SnomedHapi:Frequency", _configuration.GetValue("Terminology:SnomedHapi:Frequency", "Monthly"), "SNOMED CT terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:SnomedHapi:ExecutionTime", _configuration.GetValue("Terminology:SnomedHapi:ExecutionTime", "04:45"), "Local execution time for the scheduled SNOMED CT terminology-server sync (HH:mm)."),

            ("Terminology:Icd10PcsHapi:SchedulerEnabled", Bool("Terminology:Icd10PcsHapi:SchedulerEnabled", false), "Master switch for automatically downloading the latest official CMS ICD-10-PCS order file and loading it into the terminology server, on a schedule."),
            ("Terminology:Icd10PcsHapi:Frequency", _configuration.GetValue("Terminology:Icd10PcsHapi:Frequency", "Monthly"), "ICD-10-PCS terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:Icd10PcsHapi:ExecutionTime", _configuration.GetValue("Terminology:Icd10PcsHapi:ExecutionTime", "05:00"), "Local execution time for the scheduled ICD-10-PCS terminology-server sync (HH:mm)."),

            ("Terminology:MeshHapi:SchedulerEnabled", Bool("Terminology:MeshHapi:SchedulerEnabled", false), "Master switch for automatically downloading the latest official NLM MeSH descriptor file and loading it into the terminology server, on a schedule."),
            ("Terminology:MeshHapi:Frequency", _configuration.GetValue("Terminology:MeshHapi:Frequency", "Monthly"), "MeSH terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:MeshHapi:ExecutionTime", _configuration.GetValue("Terminology:MeshHapi:ExecutionTime", "05:15"), "Local execution time for the scheduled MeSH terminology-server sync (HH:mm)."),

            ("Terminology:DcmHapi:SchedulerEnabled", Bool("Terminology:DcmHapi:SchedulerEnabled", false), "Master switch for automatically downloading the official DICOM Controlled Terminology (DCM) ontology and loading it into the terminology server, on a schedule."),
            ("Terminology:DcmHapi:Frequency", _configuration.GetValue("Terminology:DcmHapi:Frequency", "Monthly"), "DCM terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:DcmHapi:ExecutionTime", _configuration.GetValue("Terminology:DcmHapi:ExecutionTime", "05:30"), "Local execution time for the scheduled DCM terminology-server sync (HH:mm)."),

            ("Terminology:Icpc3Hapi:SchedulerEnabled", Bool("Terminology:Icpc3Hapi:SchedulerEnabled", false), "Master switch for automatically downloading the official ICPC-3 dataset and loading it into the terminology server, on a schedule."),
            ("Terminology:Icpc3Hapi:Frequency", _configuration.GetValue("Terminology:Icpc3Hapi:Frequency", "Monthly"), "ICPC-3 terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:Icpc3Hapi:ExecutionTime", _configuration.GetValue("Terminology:Icpc3Hapi:ExecutionTime", "05:45"), "Local execution time for the scheduled ICPC-3 terminology-server sync (HH:mm)."),

            ("Terminology:Icd11Hapi:SchedulerEnabled", Bool("Terminology:Icd11Hapi:SchedulerEnabled", false), "Master switch for automatically downloading the official WHO ICD-11 MMS linearization export and loading it into the terminology server, on a schedule."),
            ("Terminology:Icd11Hapi:Frequency", _configuration.GetValue("Terminology:Icd11Hapi:Frequency", "Monthly"), "ICD-11 MMS terminology-server sync frequency: Weekly or Monthly."),
            ("Terminology:Icd11Hapi:ExecutionTime", _configuration.GetValue("Terminology:Icd11Hapi:ExecutionTime", "06:00"), "Local execution time for the scheduled ICD-11 MMS terminology-server sync (HH:mm)."),

            ("Terminology:Ndc:SchedulerEnabled", Bool("Terminology:Ndc:SchedulerEnabled", false), "Master switch for scheduled NDC synchronization."),
            ("Terminology:Ndc:Frequency", _configuration.GetValue("Terminology:Ndc:Frequency", "Daily"), "NDC synchronization frequency: Daily or Weekly. openFDA's NDC Directory updates daily."),
            ("Terminology:Ndc:ExecutionTime", _configuration.GetValue("Terminology:Ndc:ExecutionTime", "04:00"), "Local execution time for scheduled NDC synchronization (HH:mm)."),

            ("Terminology:Ucum:SchedulerEnabled", Bool("Terminology:Ucum:SchedulerEnabled", false), "Master switch for scheduled UCUM synchronization."),
            ("Terminology:Ucum:Frequency", _configuration.GetValue("Terminology:Ucum:Frequency", "Weekly"), "UCUM synchronization frequency: Weekly or Monthly."),
            ("Terminology:Ucum:ExecutionTime", _configuration.GetValue("Terminology:Ucum:ExecutionTime", "05:00"), "Local execution time for scheduled UCUM synchronization (HH:mm)."),

            ("Terminology:RxNorm:SchedulerEnabled", Bool("Terminology:RxNorm:SchedulerEnabled", false), "Master switch for scheduled RxNorm synchronization."),
            ("Terminology:RxNorm:ExecutionTime", _configuration.GetValue("Terminology:RxNorm:ExecutionTime", "02:00"), "Local execution time for scheduled RxNorm synchronization (HH:mm). RxNorm has a fixed monthly cadence (1st of each month) — no Frequency setting."),

            ("Terminology:Snomed:SchedulerEnabled", Bool("Terminology:Snomed:SchedulerEnabled", false), "Master switch for scheduled SNOMED CT synchronization."),
            ("Terminology:Snomed:ExecutionTime", _configuration.GetValue("Terminology:Snomed:ExecutionTime", "03:00"), "Local execution time for scheduled SNOMED CT synchronization (HH:mm). SNOMED CT US Edition releases twice yearly (March and September) — no Frequency setting."),

            ("RateLimiting:Enabled", Bool("RateLimiting:Enabled", true), "Master on/off for API rate limiting. Requires a restart to take effect."),
            ("RateLimiting:Auth:PermitPerWindow", Int("RateLimiting:Auth:PermitPerWindow", 10), "Auth endpoint rate-limit permit count. Requires a restart to take effect."),
            ("RateLimiting:Auth:WindowMinutes", Int("RateLimiting:Auth:WindowMinutes", 5), "Auth endpoint rate-limit window, in minutes. Requires a restart to take effect."),
            ("RateLimiting:OAuth:PermitPerWindow", Int("RateLimiting:OAuth:PermitPerWindow", 30), "OAuth endpoint rate-limit permit count. Requires a restart to take effect."),
            ("RateLimiting:OAuth:WindowMinutes", Int("RateLimiting:OAuth:WindowMinutes", 5), "OAuth endpoint rate-limit window, in minutes. Requires a restart to take effect."),
            ("RateLimiting:Webhook:PermitPerWindow", Int("RateLimiting:Webhook:PermitPerWindow", 120), "Webhook endpoint rate-limit permit count. Requires a restart to take effect."),
            ("RateLimiting:Webhook:WindowMinutes", Int("RateLimiting:Webhook:WindowMinutes", 1), "Webhook endpoint rate-limit window, in minutes. Requires a restart to take effect."),

            ("Mfa:Issuer", _configuration.GetValue("Mfa:Issuer", "Segue"), "Issuer label shown in the authenticator app."),
            ("Mfa:BackupCodeCount", Int("Mfa:BackupCodeCount", 10), "Number of one-time backup codes generated at MFA enrollment."),
            ("LocalAuth:Lockout:MaxFailedAttempts", Int("LocalAuth:Lockout:MaxFailedAttempts", 5), "Consecutive failed logins that trigger an account lockout."),
            ("LocalAuth:Lockout:LockoutMinutes", Int("LocalAuth:Lockout:LockoutMinutes", 15), "How long an account stays locked once the threshold is reached."),
            ("Authentication:TokenLifetimeMinutes", Int("Authentication:TokenLifetimeMinutes", 60), "JWT access token lifetime, in minutes."),
            ("Authentication:RefreshTokenLifetimeDays", Int("Authentication:RefreshTokenLifetimeDays", 30), "Refresh token lifetime, in days."),

            ("WebhookIngestion:Async", Bool("WebhookIngestion:Async", false), "When true, webhook ingestion is queued instead of processed inline."),
            ("WebhookIngestion:RequireSignature", Bool("WebhookIngestion:RequireSignature", true), "When true, inbound webhooks must present a valid HMAC signature."),
            ("WebhookIngestion:SignatureHeader", _configuration.GetValue("WebhookIngestion:SignatureHeader", "X-FHIRBridge-Signature"), "HTTP header expected to carry the webhook signature."),

            ("Workflow:GraphExecution:Enabled", Bool("Workflow:GraphExecution:Enabled", false), "Master switch for running the persisted workflow graph instead of the flat route path."),
            (TransformationRulesFeatureFlag.SettingKey, Bool(TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden), "When true, hides the destination wizard's Rules button and the Settings > Transformation Rules screen. Does NOT stop already-configured rules from running during workflow execution. Ships hidden by default; set to false to reveal it."),
            ("Compliance:RequireTde", Bool("Compliance:RequireTde", false), "When true, the TDE health check reports Unhealthy (not just Degraded) if the database is unencrypted."),

            (
                "OAuth:PublicBaseUrl",
                _configuration.GetValue("OAuth:PublicBaseUrl", string.Empty),
                "The public HTTPS origin (e.g. https://your-domain.example.com) OAuth redirect/callback/launch URLs "
                + "are built from — must exactly match what's registered with each EHR. Leave blank to derive it "
                + "from the incoming request instead, which is only reliable with no WAF/reverse proxy in front."),
        };

        foreach (var (key, value, description) in defaults)
        {
            var existing = await _repository.GetByKeyAsync(key, cancellationToken);
            if (existing is not null)
            {
                continue;
            }

            await _repository.UpsertAsync(key, value, description, cancellationToken);
        }
    }

    private string Bool(string key, bool defaultValue) =>
        _configuration.GetValue(key, defaultValue).ToString();

    private string Int(string key, int defaultValue) =>
        _configuration.GetValue(key, defaultValue).ToString();

    private string Double(string key, double defaultValue) =>
        _configuration.GetValue(key, defaultValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
