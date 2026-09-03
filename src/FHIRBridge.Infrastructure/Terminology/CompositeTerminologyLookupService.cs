using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

public sealed class CompositeTerminologyLookupService : ITerminologyLookupService
{
    public const string DisableNetworkFallbackSettingKey = "Terminology:DisableNetworkFallback";

    private readonly HapiLocalTerminologyLookupService _hapiLocalLookupService;
    private readonly LocalTerminologyLookupService _localLookupService;
    private readonly LoincTerminologyLookupService _loincLookupService;
    private readonly Icd10TerminologyLookupService _icd10LookupService;
    private readonly SnomedTerminologyLookupService _snomedLookupService;
    private readonly RxNormTerminologyLookupService _rxNormLookupService;
    private readonly FhirTerminologyLookupService _fhirLookupService;
    private readonly ISystemSettingsCache _settingsCache;

    public CompositeTerminologyLookupService(
        HapiLocalTerminologyLookupService hapiLocalLookupService,
        LocalTerminologyLookupService localLookupService,
        LoincTerminologyLookupService loincLookupService,
        Icd10TerminologyLookupService icd10LookupService,
        SnomedTerminologyLookupService snomedLookupService,
        RxNormTerminologyLookupService rxNormLookupService,
        FhirTerminologyLookupService fhirLookupService,
        ISystemSettingsCache settingsCache)
    {
        _hapiLocalLookupService = hapiLocalLookupService;
        _localLookupService = localLookupService;
        _loincLookupService = loincLookupService;
        _icd10LookupService = icd10LookupService;
        _snomedLookupService = snomedLookupService;
        _rxNormLookupService = rxNormLookupService;
        _fhirLookupService = fhirLookupService;
        _settingsCache = settingsCache;
    }

    public async Task<TerminologyLookupResult?> LookupAsync(
        string system,
        string code,
        CancellationToken cancellationToken)
    {
        var local = await _hapiLocalLookupService.LookupAsync(system, code, cancellationToken)
            ?? await _localLookupService.LookupAsync(system, code, cancellationToken)
            ?? await _loincLookupService.LookupAsync(system, code, cancellationToken)
            ?? await _icd10LookupService.LookupAsync(system, code, cancellationToken)
            ?? await _snomedLookupService.LookupAsync(system, code, cancellationToken)
            ?? await _rxNormLookupService.LookupAsync(system, code, cancellationToken);
        if (local is not null)
        {
            return local;
        }

        var networkDisabled = await _settingsCache.GetBoolAsync(DisableNetworkFallbackSettingKey, false, cancellationToken);
        return networkDisabled ? null : await _fhirLookupService.LookupAsync(system, code, cancellationToken);
    }

    public Task<TerminologyLookupResult?> LookupAnyLocalSystemAsync(string code, CancellationToken cancellationToken) =>
        _hapiLocalLookupService.LookupAnySystemAsync(code, cancellationToken);
}
