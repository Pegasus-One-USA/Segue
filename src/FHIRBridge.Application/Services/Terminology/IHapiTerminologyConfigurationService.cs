using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services.Terminology;

/// <summary>Grouped settings, manual trigger, and run history for the 13 HAPI-terminology-server sync
/// systems (implementation lives in Infrastructure since it resolves the per-system
/// IHapi{Code}TerminologySyncService instances, which are Infrastructure-only types).</summary>
public interface IHapiTerminologyConfigurationService
{
    Task<IReadOnlyList<HapiTerminologyConfigurationDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<HapiTerminologyConfigurationDto> GetAsync(string code, CancellationToken cancellationToken);

    Task<HapiTerminologyConfigurationDto> UpdateAsync(
        string code, UpdateHapiTerminologyConfigurationRequest request, CancellationToken cancellationToken);

    /// <summary>Runs the sync immediately, recording a history row (Running, then Succeeded/Failed).
    /// Intended to be invoked from a background job (see TerminologyImportChannel), not inline on the
    /// request thread.</summary>
    Task RunAndRecordHistoryAsync(string code, CancellationToken cancellationToken);

    Task<IReadOnlyList<HapiTerminologyImportHistoryEntryDto>> GetHistoryAsync(string code, CancellationToken cancellationToken);
}
