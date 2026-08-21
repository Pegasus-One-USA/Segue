using FHIRBridge.Application.Abstractions.Terminology;

namespace FHIRBridge.Infrastructure.Terminology;

public sealed class UcumSynchronizationService : IUcumSynchronizationService
{
    private readonly IUcumReleaseClient _releaseClient;
    private readonly IUcumImportService _importService;

    public UcumSynchronizationService(IUcumReleaseClient releaseClient, IUcumImportService importService)
        => (_releaseClient, _importService) = (releaseClient, importService);

    public async Task<UcumImportResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var release = await _releaseClient.GetCurrentReleaseAsync(cancellationToken);
        var filePath = await _releaseClient.DownloadReleaseAsync(release, cancellationToken);
        return await _importService.ImportAsync(filePath, cancellationToken);
    }
}
