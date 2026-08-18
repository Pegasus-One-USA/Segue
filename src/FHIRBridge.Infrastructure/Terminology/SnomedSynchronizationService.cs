using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Auto-sync entry point for SNOMED CT (US Edition): fetches the current release download URL from NLM's UTS
/// <c>/releases</c> API, downloads it, then hands off to the existing, unchanged <see cref="ISnomedImportService"/>
/// — the same RF2 parsing path the manual-upload flow already uses.
/// </summary>
public sealed class SnomedSynchronizationService : ISnomedSynchronizationService
{
    private const string ReleaseType = "snomed-ct-us-edition";

    private readonly IUtsReleaseClient _releaseClient;
    private readonly ISnomedImportService _importService;

    public SnomedSynchronizationService(IUtsReleaseClient releaseClient, ISnomedImportService importService)
        => (_releaseClient, _importService) = (releaseClient, importService);

    public async Task<SnomedImportResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var release = await _releaseClient.GetCurrentReleaseAsync(ReleaseType, cancellationToken);
        var zipPath = await _releaseClient.DownloadReleaseAsync(release, "Snomed", cancellationToken);
        return await _importService.ImportAsync(zipPath, cancellationToken);
    }
}
