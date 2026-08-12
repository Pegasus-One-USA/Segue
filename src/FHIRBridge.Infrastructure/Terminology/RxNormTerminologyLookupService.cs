using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Reads active RxNorm concepts from the local terminology schema; it never calls the network.</summary>
public sealed class RxNormTerminologyLookupService
{
    public const string CanonicalSystem = "http://www.nlm.nih.gov/research/umls/rxnorm";
    private readonly FHIRBridgeDbContext _dbContext;

    public RxNormTerminologyLookupService(FHIRBridgeDbContext dbContext) => _dbContext = dbContext;

    public async Task<TerminologyLookupResult?> LookupAsync(string system, string code, CancellationToken cancellationToken)
    {
        if (!IsRxNorm(system) || string.IsNullOrWhiteSpace(code)) return null;

        var concept = await _dbContext.RxNormConcepts.AsNoTracking()
            .Where(x => x.Rxcui == code.Trim() && x.IsActive)
            .Select(x => new { x.Rxcui, x.Name, x.Version })
            .SingleOrDefaultAsync(cancellationToken);

        return concept is null ? null : new TerminologyLookupResult(
            CanonicalSystem, concept.Rxcui, concept.Name, concept.Version, "RxNormDatabase");
    }

    private static bool IsRxNorm(string system) =>
        string.Equals(system?.TrimEnd('/'), CanonicalSystem, StringComparison.OrdinalIgnoreCase)
        || string.Equals(system, "urn:oid:2.16.840.1.113883.6.88", StringComparison.OrdinalIgnoreCase);
}
