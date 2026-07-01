using System.Text.Json;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.Caching.Distributed;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Caches code-system lookups (system + code → display) in the distributed cache. Terminology is effectively static
/// per code version, and a pipeline run resolves the same codes repeatedly across resources, so caching the resolved
/// result removes redundant local/FHIR-server work. Only positive hits are cached; a miss falls through every time so
/// a later-loaded ConceptMap/CodeSystem is picked up without a stale negative entry.
/// </summary>
public sealed class CachingTerminologyLookupService : ITerminologyLookupService
{
    private const string KeyPrefix = "term:lookup:";

    private readonly ITerminologyLookupService _inner;
    private readonly IDistributedCache _cache;
    private readonly DistributedCacheEntryOptions _entryOptions;

    public CachingTerminologyLookupService(
        ITerminologyLookupService inner,
        IDistributedCache cache,
        TimeSpan timeToLive)
    {
        _inner = inner;
        _cache = cache;
        _entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = timeToLive };
    }

    public async Task<TerminologyLookupResult?> LookupAsync(string system, string code, CancellationToken cancellationToken)
    {
        var cacheKey = $"{KeyPrefix}{system}|{code}";

        var cached = await _cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<TerminologyLookupResult>(cached);
        }

        var result = await _inner.LookupAsync(system, code, cancellationToken);
        if (result is not null)
        {
            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result), _entryOptions, cancellationToken);
        }

        return result;
    }
}
