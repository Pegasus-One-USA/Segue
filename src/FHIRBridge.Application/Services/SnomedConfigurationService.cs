using System.Globalization;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Services;

/// <summary>Owns SNOMED CT's auto-sync scheduler settings. The UTS API key itself is shared with RxNorm — see
/// <see cref="UtsCredentialNames"/> — so this service only ever writes it, never claims sole ownership of it.</summary>
public sealed class SnomedConfigurationService : ISnomedConfigurationService
{
    private readonly ISystemSettingsCache _settings;
    private readonly ISystemSettingsService _settingsService;
    private readonly ISecretWriter _secretWriter;
    private readonly IAppSecretMetadataProvider _metadataProvider;

    public SnomedConfigurationService(ISystemSettingsCache settings, ISystemSettingsService settingsService,
        ISecretWriter secretWriter, IAppSecretMetadataProvider metadataProvider)
    {
        _settings = settings;
        _settingsService = settingsService;
        _secretWriter = secretWriter;
        _metadataProvider = metadataProvider;
    }

    public async Task<SnomedConfigurationDto> GetAsync(CancellationToken cancellationToken)
    {
        var apiKey = await _metadataProvider.GetMetadataAsync(new SecretReference(UtsCredentialNames.VaultName, UtsCredentialNames.ApiKeySecretName), cancellationToken);
        return new SnomedConfigurationDto(
            apiKey.Provisioned,
            await _settings.GetBoolAsync("Terminology:Snomed:SchedulerEnabled", false, cancellationToken),
            await _settings.GetStringAsync("Terminology:Snomed:ExecutionTime", "03:00", cancellationToken),
            await _settings.GetIntAsync("Terminology:Snomed:RetryCount", 3, cancellationToken),
            await _settings.GetIntAsync("Terminology:Snomed:RetryIntervalSeconds", 60, cancellationToken));
    }

    public async Task<SnomedConfigurationDto> UpdateAsync(UpdateSnomedConfigurationRequest request, CancellationToken cancellationToken)
    {
        if (!TimeOnly.TryParseExact(request.ExecutionTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Execution time must use HH:mm.");
        if (request.RetryCount is < 0 or > 10 || request.RetryIntervalSeconds is < 1 or > 3600)
            throw new InvalidOperationException("SNOMED CT retry settings are outside their supported range.");

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            await _secretWriter.WriteSecretAsync(new SecretReference(UtsCredentialNames.VaultName, UtsCredentialNames.ApiKeySecretName), request.ApiKey, cancellationToken);

        await _settingsService.SetAsync("Terminology:Snomed:SchedulerEnabled", request.SchedulerEnabled.ToString(), "Master switch for scheduled SNOMED CT synchronization.", cancellationToken);
        await _settingsService.SetAsync("Terminology:Snomed:ExecutionTime", request.ExecutionTime, "Local execution time for scheduled SNOMED CT synchronization.", cancellationToken);
        await _settingsService.SetAsync("Terminology:Snomed:RetryCount", request.RetryCount.ToString(CultureInfo.InvariantCulture), "SNOMED CT synchronization retry count.", cancellationToken);
        await _settingsService.SetAsync("Terminology:Snomed:RetryIntervalSeconds", request.RetryIntervalSeconds.ToString(CultureInfo.InvariantCulture), "Seconds between SNOMED CT synchronization retries.", cancellationToken);
        return await GetAsync(cancellationToken);
    }
}
