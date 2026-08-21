using System.Globalization;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Services;

/// <summary>Owns the global LOINC settings and write-only ProvisionedSecrets used by its download client.</summary>
public sealed class LoincConfigurationService : ILoincConfigurationService
{
    public const string VaultName = "app";
    public const string UsernameSecretName = "loinc-basic-username";
    public const string PasswordSecretName = "loinc-basic-password";

    private readonly ISystemSettingsCache _settings;
    private readonly ISystemSettingsService _settingsService;
    private readonly ISecretWriter _secretWriter;
    private readonly IAppSecretMetadataProvider _metadataProvider;

    public LoincConfigurationService(ISystemSettingsCache settings, ISystemSettingsService settingsService,
        ISecretWriter secretWriter, IAppSecretMetadataProvider metadataProvider)
    {
        _settings = settings;
        _settingsService = settingsService;
        _secretWriter = secretWriter;
        _metadataProvider = metadataProvider;
    }

    public async Task<LoincConfigurationDto> GetAsync(CancellationToken cancellationToken)
    {
        var username = await _metadataProvider.GetMetadataAsync(new SecretReference(VaultName, UsernameSecretName), cancellationToken);
        var password = await _metadataProvider.GetMetadataAsync(new SecretReference(VaultName, PasswordSecretName), cancellationToken);
        return new LoincConfigurationDto(
            await Get("Terminology:Loinc:DownloadApiUrl", string.Empty, cancellationToken),
            await Get("Terminology:Loinc:FhirApiUrl", string.Empty, cancellationToken),
            UsernameSecretName, PasswordSecretName, username.Provisioned, password.Provisioned,
            await _settings.GetBoolAsync("Terminology:Loinc:SchedulerEnabled", false, cancellationToken),
            await Get("Terminology:Loinc:Frequency", "Monthly", cancellationToken),
            await Get("Terminology:Loinc:ExecutionTime", "02:00", cancellationToken),
            await _settings.GetIntAsync("Terminology:Loinc:RetryCount", 3, cancellationToken),
            await _settings.GetIntAsync("Terminology:Loinc:RetryIntervalSeconds", 60, cancellationToken),
            await _settings.GetIntAsync("Terminology:Loinc:DownloadTimeoutSeconds", 900, cancellationToken));
    }

    public async Task<LoincConfigurationDto> UpdateAsync(UpdateLoincConfigurationRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.DownloadApiUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("A valid LOINC download URL is required.");
        if (!string.IsNullOrWhiteSpace(request.FhirApiUrl) && !Uri.TryCreate(request.FhirApiUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("The LOINC FHIR URL must be absolute.");
        if (!string.Equals(request.Frequency, "Weekly", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Frequency, "Monthly", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Frequency must be Weekly or Monthly.");
        if (!TimeOnly.TryParseExact(request.ExecutionTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new InvalidOperationException("Execution time must use HH:mm.");
        if (request.RetryCount is < 0 or > 10 || request.RetryIntervalSeconds is < 1 or > 3600 || request.DownloadTimeoutSeconds is < 30 or > 3600) throw new InvalidOperationException("LOINC retry and timeout settings are outside their supported range.");

        if (!string.IsNullOrWhiteSpace(request.Username)) await _secretWriter.WriteSecretAsync(new SecretReference(VaultName, UsernameSecretName), request.Username, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.Password)) await _secretWriter.WriteSecretAsync(new SecretReference(VaultName, PasswordSecretName), request.Password, cancellationToken);

        await Set("Terminology:Loinc:DownloadApiUrl", request.DownloadApiUrl.Trim(), "Official LOINC release-download endpoint.", cancellationToken);
        await Set("Terminology:Loinc:FhirApiUrl", request.FhirApiUrl?.Trim() ?? string.Empty, "Optional official LOINC FHIR terminology endpoint.", cancellationToken);
        await Set("Terminology:Loinc:SchedulerEnabled", request.SchedulerEnabled.ToString(), "Master switch for scheduled LOINC synchronization.", cancellationToken);
        await Set("Terminology:Loinc:Frequency", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Frequency.ToLowerInvariant()), "LOINC synchronization frequency.", cancellationToken);
        await Set("Terminology:Loinc:ExecutionTime", request.ExecutionTime, "Local execution time for scheduled LOINC synchronization.", cancellationToken);
        await Set("Terminology:Loinc:RetryCount", request.RetryCount.ToString(CultureInfo.InvariantCulture), "LOINC synchronization retry count.", cancellationToken);
        await Set("Terminology:Loinc:RetryIntervalSeconds", request.RetryIntervalSeconds.ToString(CultureInfo.InvariantCulture), "Seconds between LOINC synchronization retries.", cancellationToken);
        await Set("Terminology:Loinc:DownloadTimeoutSeconds", request.DownloadTimeoutSeconds.ToString(CultureInfo.InvariantCulture), "Maximum LOINC release download duration.", cancellationToken);
        return await GetAsync(cancellationToken);
    }

    private Task<string> Get(string key, string fallback, CancellationToken cancellationToken) => _settings.GetStringAsync(key, fallback, cancellationToken);
    private Task Set(string key, string value, string description, CancellationToken cancellationToken) => _settingsService.SetAsync(key, value, description, cancellationToken);
}
