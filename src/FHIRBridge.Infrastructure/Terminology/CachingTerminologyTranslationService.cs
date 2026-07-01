using System.Text.Json;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.Caching.Distributed;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Caches code translations (source system/code → target system/code) in the distributed cache. ConceptMap/$translate
/// results are stable per map version and re-requested for the same codes across a run, so caching positive results
/// avoids repeated local/FHIR-server translation work. Misses are not cached so a newly loaded ConceptMap is honoured.
/// </summary>
public sealed class CachingTerminologyTranslationService : ITerminologyTranslationService
{
    private const string KeyPrefix = "term:translate:";

    private readonly ITerminologyTranslationService _inner;
    private readonly IDistributedCache _cache;
    private readonly DistributedCacheEntryOptions _entryOptions;

    public CachingTerminologyTranslationService(
        ITerminologyTranslationService inner,
        IDistributedCache cache,
        TimeSpan timeToLive)
    {
        _inner = inner;
        _cache = cache;
        _entryOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = timeToLive };
    }

    public async Task<TerminologyTranslationResult?> TranslateAsync(
        string sourceSystem,
        string sourceCode,
        string targetSystem,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{KeyPrefix}{sourceSystem}|{sourceCode}|{targetSystem}";

        var cached = await _cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<TerminologyTranslationResult>(cached);
        }

        var result = await _inner.TranslateAsync(sourceSystem, sourceCode, targetSystem, cancellationToken);
        if (result is not null)
        {
            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result), _entryOptions, cancellationToken);
        }

        return result;
    }
}
