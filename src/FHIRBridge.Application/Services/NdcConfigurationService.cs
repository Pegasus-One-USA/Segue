using System.Globalization;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Services;

public sealed class NdcConfigurationService : INdcConfigurationService
{
    public const string VaultName = "app";
    public const string ApiKeySecretName = "ndc-api-key";

    private readonly ISystemSettingsCache _settings;
    private readonly ISystemSettingsService _settingsService;
    private readonly ISecretWriter _secretWriter;
    private readonly IAppSecretMetadataProvider _metadataProvider;

    public NdcConfigurationService(ISystemSettingsCache settings, ISystemSettingsService settingsService,
        ISecretWriter secretWriter, IAppSecretMetadataProvider metadataProvider)
    {
        _settings = settings;
        _settingsService = settingsService;
        _secretWriter = secretWriter;
        _metadataProvider = metadataProvider;
    }

    public async Task<NdcConfigurationDto> GetAsync(CancellationToken cancellationToken)
    {
        var apiKey = await _metadataProvider.GetMetadataAsync(new SecretReference(VaultName, ApiKeySecretName), cancellationToken);
        return new NdcConfigurationDto(
            apiKey.Provisioned,
            await _settings.GetBoolAsync("Terminology:Ndc:SchedulerEnabled", false, cancellationToken),
            await _settings.GetStringAsync("Terminology:Ndc:Frequency", "Daily", cancellationToken),
            await _settings.GetStringAsync("Terminology:Ndc:ExecutionTime", "04:00", cancellationToken));
    }

    public async Task<NdcConfigurationDto> UpdateAsync(UpdateNdcConfigurationRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Frequency, "Daily", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Frequency, "Weekly", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Frequency must be Daily or Weekly.");
        if (!TimeOnly.TryParseExact(request.ExecutionTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Execution time must use HH:mm.");

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            await _secretWriter.WriteSecretAsync(new SecretReference(VaultName, ApiKeySecretName), request.ApiKey, cancellationToken);

        await _settingsService.SetAsync("Terminology:Ndc:SchedulerEnabled", request.SchedulerEnabled.ToString(), "Master switch for scheduled NDC synchronization.", cancellationToken);
        await _settingsService.SetAsync("Terminology:Ndc:Frequency", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Frequency.ToLowerInvariant()), "NDC synchronization frequency.", cancellationToken);
        await _settingsService.SetAsync("Terminology:Ndc:ExecutionTime", request.ExecutionTime, "Local execution time for scheduled NDC synchronization.", cancellationToken);
        return await GetAsync(cancellationToken);
    }
}
