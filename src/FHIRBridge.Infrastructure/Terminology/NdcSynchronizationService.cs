using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Infrastructure.Terminology;

public sealed class NdcSynchronizationService : INdcSynchronizationService
{
    private readonly INdcReleaseClient _releaseClient;
    private readonly INdcImportService _importService;

    public NdcSynchronizationService(INdcReleaseClient releaseClient, INdcImportService importService)
        => (_releaseClient, _importService) = (releaseClient, importService);

    public async Task<NdcImportResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var release = await _releaseClient.GetCurrentReleaseAsync(cancellationToken);
        var zipPath = await _releaseClient.DownloadReleaseAsync(release, cancellationToken);
        return await _importService.ImportAsync(zipPath, cancellationToken);
    }
}
