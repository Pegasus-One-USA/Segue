using FHIRBridge.Application.Abstractions.Persistence;
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

            ("RuntimeWorker:Enabled", Bool("RuntimeWorker:Enabled", false), "Master on/off for the scheduled Phase 1 runtime worker."),
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
            ("Compliance:RequireTde", Bool("Compliance:RequireTde", false), "When true, the TDE health check reports Unhealthy (not just Degraded) if the database is unencrypted."),
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
