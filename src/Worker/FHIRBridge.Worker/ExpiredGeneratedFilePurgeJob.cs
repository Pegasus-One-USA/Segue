using FHIRBridge.Infrastructure.Destinations;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// HIPAA #6: proactively deletes generated-file-download artifacts (CSV/PDF exports) past their retention window,
/// instead of relying solely on next-access cleanup in <c>GeneratedFileDownloadLinkService.TryResolveAsync</c> —
/// a file whose signed link is never followed again would otherwise sit on disk indefinitely.
/// </summary>
public sealed class ExpiredGeneratedFilePurgeJob : BackgroundService
{
    // Generous upper bound beyond any individual link's own (much shorter) expiry — a safety net for files whose
    // link is never followed again, not the primary expiry mechanism (that's still the per-token ExpiryUtc).
    private static readonly TimeSpan MaxFileAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(1);

    private readonly GeneratedFileDownloadOptions _options;
    private readonly ILogger<ExpiredGeneratedFilePurgeJob> _logger;

    public ExpiredGeneratedFilePurgeJob(
        IOptions<GeneratedFileDownloadOptions> options,
        ILogger<ExpiredGeneratedFilePurgeJob> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rootPath = Path.IsPathRooted(_options.RootPath)
            ? _options.RootPath
            : Path.Combine(AppContext.BaseDirectory, _options.RootPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PurgeExpiredFiles(rootPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Generated-file purge sweep failed for {RootPath}", rootPath);
            }

            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    private void PurgeExpiredFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        var cutoffUtc = DateTime.UtcNow - MaxFileAge;
        var purged = 0;

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
            {
                File.Delete(file);
                purged++;
            }
        }

        if (purged > 0)
        {
            _logger.LogInformation("Generated-file purge removed {Count} expired file(s) from {RootPath}", purged, rootPath);
        }
    }
}
