using System.Globalization;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <inheritdoc cref="IHapiTerminologyConfigurationService"/>
public sealed class HapiTerminologyConfigurationService : IHapiTerminologyConfigurationService
{
    private readonly HapiTerminologySystemRegistry _registry;
    private readonly ISystemSettingsCache _settings;
    private readonly ISystemSettingsService _settingsService;
    private readonly ISecretWriter _secretWriter;
    private readonly IAppSecretMetadataProvider _metadataProvider;
    private readonly FHIRBridgeDbContext _db;
    private readonly IServiceProvider _serviceProvider;

    public HapiTerminologyConfigurationService(
        HapiTerminologySystemRegistry registry,
        ISystemSettingsCache settings,
        ISystemSettingsService settingsService,
        ISecretWriter secretWriter,
        IAppSecretMetadataProvider metadataProvider,
        FHIRBridgeDbContext db,
        IServiceProvider serviceProvider)
    {
        _registry = registry;
        _settings = settings;
        _settingsService = settingsService;
        _secretWriter = secretWriter;
        _metadataProvider = metadataProvider;
        _db = db;
        _serviceProvider = serviceProvider;
    }

    public async Task<IReadOnlyList<HapiTerminologyConfigurationDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<HapiTerminologyConfigurationDto>(_registry.All.Count);
        foreach (var descriptor in _registry.All)
        {
            result.Add(await BuildDtoAsync(descriptor, cancellationToken));
        }

        return result;
    }

    public async Task<HapiTerminologyConfigurationDto> GetAsync(string code, CancellationToken cancellationToken) =>
        await BuildDtoAsync(_registry.Get(code), cancellationToken);

    public async Task<HapiTerminologyConfigurationDto> UpdateAsync(
        string code, UpdateHapiTerminologyConfigurationRequest request, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(code);

        if (!string.Equals(request.Frequency, "Weekly", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Frequency, "Monthly", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Frequency must be Weekly or Monthly.");
        }

        if (!TimeOnly.TryParseExact(request.ExecutionTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new InvalidOperationException("Execution time must use HH:mm.");
        }

        if (descriptor.DownloadApiUrlSettingKey is not null && !string.IsNullOrWhiteSpace(request.DownloadApiUrl))
        {
            if (!Uri.TryCreate(request.DownloadApiUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"The {descriptor.DisplayName} download URL must be absolute.");
            }

            await _settingsService.SetAsync(descriptor.DownloadApiUrlSettingKey, request.DownloadApiUrl.Trim(),
                $"Official {descriptor.DisplayName} release-download endpoint (shared by the HAPI sync and the legacy database sync).", cancellationToken);
        }

        await WriteCredentialsAsync(descriptor, request.CredentialValues, cancellationToken);

        var prefix = descriptor.SettingsKeyPrefix;
        await _settingsService.SetAsync($"{prefix}:SchedulerEnabled", request.SchedulerEnabled.ToString(),
            $"Master switch for automatically downloading {descriptor.DisplayName} and loading it into the terminology server, on a schedule.", cancellationToken);
        await _settingsService.SetAsync($"{prefix}:Frequency",
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Frequency.ToLowerInvariant()),
            $"{descriptor.DisplayName} terminology-server sync frequency: Weekly or Monthly.", cancellationToken);
        await _settingsService.SetAsync($"{prefix}:ExecutionTime", request.ExecutionTime,
            $"Local execution time for the scheduled {descriptor.DisplayName} terminology-server sync (HH:mm).", cancellationToken);

        return await BuildDtoAsync(descriptor, cancellationToken);
    }

    public async Task RunAndRecordHistoryAsync(string code, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(code);
        var history = new HapiTerminologyImportHistory(descriptor.Code);
        _db.HapiTerminologyImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var outcome = await descriptor.RunAsync(_serviceProvider, cancellationToken);
            history.Complete(outcome.Count, outcome.Version);
            await _settingsService.SetAsync($"{descriptor.SettingsKeyPrefix}:LastRunUtc", DateTime.UtcNow.ToString("O"), null, cancellationToken);
        }
        catch (Exception exception)
        {
            history.Fail(exception.Message);
        }
        finally
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<HapiTerminologyImportHistoryEntryDto>> GetHistoryAsync(string code, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(code);
        return await _db.HapiTerminologyImportHistory
            .Where(x => x.CodeSystem == descriptor.Code)
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new HapiTerminologyImportHistoryEntryDto(
                x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
    }

    private async Task<HapiTerminologyConfigurationDto> BuildDtoAsync(HapiTerminologySystemDescriptor descriptor, CancellationToken cancellationToken)
    {
        var prefix = descriptor.SettingsKeyPrefix;
        var schedulerEnabled = await _settings.GetBoolAsync($"{prefix}:SchedulerEnabled", false, cancellationToken);
        var frequency = await _settings.GetStringAsync($"{prefix}:Frequency", "Monthly", cancellationToken);
        var executionTime = await _settings.GetStringAsync($"{prefix}:ExecutionTime", "00:00", cancellationToken);
        var lastRunRaw = await _settings.GetStringAsync($"{prefix}:LastRunUtc", string.Empty, cancellationToken);
        var lastRunUtc = DateTime.TryParse(lastRunRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : (DateTime?)null;

        var credentials = await BuildCredentialFieldsAsync(descriptor, cancellationToken);
        var downloadApiUrl = descriptor.DownloadApiUrlSettingKey is null
            ? null
            : await _settings.GetStringAsync(descriptor.DownloadApiUrlSettingKey, string.Empty, cancellationToken);

        return new HapiTerminologyConfigurationDto(
            descriptor.Code, descriptor.DisplayName, schedulerEnabled, frequency,
            HapiTerminologySystemDescriptor.FrequencyOptions, executionTime, lastRunUtc, credentials, downloadApiUrl);
    }

    private async Task<IReadOnlyList<HapiCredentialFieldDto>> BuildCredentialFieldsAsync(
        HapiTerminologySystemDescriptor descriptor, CancellationToken cancellationToken)
    {
        switch (descriptor.CredentialKind)
        {
            case HapiCredentialKind.LoincBasicAuth:
                var username = await _metadataProvider.GetMetadataAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.UsernameSecretName), cancellationToken);
                var password = await _metadataProvider.GetMetadataAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.PasswordSecretName), cancellationToken);
                return new[]
                {
                    new HapiCredentialFieldDto("username", "Username", username.Provisioned),
                    new HapiCredentialFieldDto("password", "Password", password.Provisioned),
                };
            case HapiCredentialKind.UtsApiKey:
                var apiKey = await _metadataProvider.GetMetadataAsync(new SecretReference(UtsCredentialNames.VaultName, UtsCredentialNames.ApiKeySecretName), cancellationToken);
                return new[] { new HapiCredentialFieldDto("apiKey", "UTS API Key", apiKey.Provisioned) };
            default:
                return Array.Empty<HapiCredentialFieldDto>();
        }
    }

    private async Task WriteCredentialsAsync(
        HapiTerminologySystemDescriptor descriptor, IReadOnlyDictionary<string, string>? values, CancellationToken cancellationToken)
    {
        if (values is null || descriptor.CredentialKind == HapiCredentialKind.None)
        {
            return;
        }

        switch (descriptor.CredentialKind)
        {
            case HapiCredentialKind.LoincBasicAuth:
                if (values.TryGetValue("username", out var username) && !string.IsNullOrWhiteSpace(username))
                {
                    await _secretWriter.WriteSecretAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.UsernameSecretName), username, cancellationToken);
                }
                if (values.TryGetValue("password", out var password) && !string.IsNullOrWhiteSpace(password))
                {
                    await _secretWriter.WriteSecretAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.PasswordSecretName), password, cancellationToken);
                }
                break;
            case HapiCredentialKind.UtsApiKey:
                if (values.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
                {
                    await _secretWriter.WriteSecretAsync(new SecretReference(UtsCredentialNames.VaultName, UtsCredentialNames.ApiKeySecretName), apiKey, cancellationToken);
                }
                break;
        }
    }
}
