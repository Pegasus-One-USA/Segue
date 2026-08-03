using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Writes expiring rows to one NDJSON file per purge run (reusing the same local-disk-or-presigned-URL
/// delivery <see cref="MappedDestinationSerialization"/> already uses for destination writers) and records
/// an <see cref="ArchiveManifestEntry"/> pointing at it — so retention purge archives before it deletes.
/// </summary>
public sealed class EfGovernanceLogArchiveWriter : IGovernanceLogArchiveWriter
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<GovernanceArchiveOptions> _options;

    public EfGovernanceLogArchiveWriter(
        FHIRBridgeDbContext dbContext, IHttpClientFactory httpClientFactory, IOptions<GovernanceArchiveOptions> options)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task ArchiveAsync<TEntity>(
        string dataClass, IReadOnlyList<TEntity> rows, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var rootPath = _options.Value.RootPath;
        var fileName = $"{dataClass}-{cutoffUtc:yyyyMMddHHmmssfff}.ndjson";
        var ndjson = string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row)));

        await MappedDestinationSerialization.WriteTextTargetAsync(
            rootPath, fileName, ndjson, _httpClientFactory.CreateClient(nameof(EfGovernanceLogArchiveWriter)), cancellationToken);

        var isUrlTarget = Uri.TryCreate(rootPath, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        var location = isUrlTarget ? $"{rootPath.TrimEnd('/')}/{fileName}" : Path.Combine(rootPath, fileName);

        _dbContext.Set<ArchiveManifestEntry>().Add(new ArchiveManifestEntry(
            Guid.NewGuid(), dataClass, cutoffUtc, location, rows.Count, DateTime.UtcNow));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
