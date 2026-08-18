using System.Globalization;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>No credential to store — the ucum-org/ucum GitHub repository is public. Only scheduler settings.</summary>
public sealed class UcumConfigurationService : IUcumConfigurationService
{
    private readonly ISystemSettingsCache _settings;
    private readonly ISystemSettingsService _settingsService;

    public UcumConfigurationService(ISystemSettingsCache settings, ISystemSettingsService settingsService)
        => (_settings, _settingsService) = (settings, settingsService);

    public async Task<UcumConfigurationDto> GetAsync(CancellationToken cancellationToken) => new(
        await _settings.GetBoolAsync("Terminology:Ucum:SchedulerEnabled", false, cancellationToken),
        await _settings.GetStringAsync("Terminology:Ucum:Frequency", "Weekly", cancellationToken),
        await _settings.GetStringAsync("Terminology:Ucum:ExecutionTime", "05:00", cancellationToken));

    public async Task<UcumConfigurationDto> UpdateAsync(UpdateUcumConfigurationRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Frequency, "Weekly", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Frequency, "Monthly", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Frequency must be Weekly or Monthly.");
        if (!TimeOnly.TryParseExact(request.ExecutionTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Execution time must use HH:mm.");

        await _settingsService.SetAsync("Terminology:Ucum:SchedulerEnabled", request.SchedulerEnabled.ToString(), "Master switch for scheduled UCUM synchronization.", cancellationToken);
        await _settingsService.SetAsync("Terminology:Ucum:Frequency", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Frequency.ToLowerInvariant()), "UCUM synchronization frequency.", cancellationToken);
        await _settingsService.SetAsync("Terminology:Ucum:ExecutionTime", request.ExecutionTime, "Local execution time for scheduled UCUM synchronization.", cancellationToken);
        return await GetAsync(cancellationToken);
    }
}
