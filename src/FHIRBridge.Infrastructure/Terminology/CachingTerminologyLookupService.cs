using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
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
    private readonly ISystemSettingsCache _settingsCache;
    private readonly TimeSpan _defaultTimeToLive;

    public CachingTerminologyLookupService(
        ITerminologyLookupService inner,
        IDistributedCache cache,
        ISystemSettingsCache settingsCache,
        TimeSpan timeToLive)
    {
        _inner = inner;
        _cache = cache;
        _settingsCache = settingsCache;
        _defaultTimeToLive = timeToLive;
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
            var ttlMinutes = await _settingsCache.GetIntAsync(
                "Caching:TerminologyTtlMinutes", (int)_defaultTimeToLive.TotalMinutes, cancellationToken);
            var entryOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(ttlMinutes),
            };
            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result), entryOptions, cancellationToken);
        }

        return result;
    }

    // Not cached: this is only ever consulted on a configured-system miss, itself already a rare path, and
    // the code being searched for isn't known ahead of time — same reasoning as the "only positive hits are
    // cached" rule above, just with no system in the key to cache against in the first place.
    public Task<TerminologyLookupResult?> LookupAnyLocalSystemAsync(string code, CancellationToken cancellationToken) =>
        _inner.LookupAnyLocalSystemAsync(code, cancellationToken);
}
