using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Auto-sync entry point for RxNorm: fetches the current "Full Monthly Release" download URL from NLM's UTS
/// <c>/releases</c> API, downloads it, then hands off to the existing, unchanged <see cref="IRxNormImportService"/>
/// — the same code path the manual-upload flow already uses, so history/version tracking behaves identically
/// whether a release arrived by upload or by this scheduler.
/// </summary>
public sealed class RxNormSynchronizationService : IRxNormSynchronizationService
{
    private const string ReleaseType = "rxnorm-full-monthly-release";

    private readonly IUtsReleaseClient _releaseClient;
    private readonly IRxNormImportService _importService;

    public RxNormSynchronizationService(IUtsReleaseClient releaseClient, IRxNormImportService importService)
        => (_releaseClient, _importService) = (releaseClient, importService);

    public async Task<RxNormImportResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var release = await _releaseClient.GetCurrentReleaseAsync(ReleaseType, cancellationToken);
        var zipPath = await _releaseClient.DownloadReleaseAsync(release, "RxNorm", cancellationToken);
        return await _importService.ImportAsync(zipPath, cancellationToken);
    }
}
