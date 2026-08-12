using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Reads active SNOMED CT concepts from the local terminology schema; it never calls the network.
/// Uses the concept row's own flattened <c>PreferredTerm</c>/<c>Fsn</c> (populated at import time) rather than
/// joining <c>SnomedDescriptions</c> — same one-row-read simplification <see cref="LoincTerminologyLookupService"/>
/// uses. Revisit only if language-specific synonym selection is needed later.</summary>
public sealed class SnomedTerminologyLookupService
{
    public const string CanonicalSystem = "http://snomed.info/sct";
    private readonly FHIRBridgeDbContext _dbContext;

    public SnomedTerminologyLookupService(FHIRBridgeDbContext dbContext) => _dbContext = dbContext;

    public async Task<TerminologyLookupResult?> LookupAsync(string system, string code, CancellationToken cancellationToken)
    {
        if (!IsSnomed(system) || string.IsNullOrWhiteSpace(code)) return null;

        var concept = await _dbContext.SnomedConcepts.AsNoTracking()
            .Where(x => x.Id == code.Trim() && x.Active)
            .Select(x => new { x.Id, x.PreferredTerm, x.Fsn, x.Version })
            .SingleOrDefaultAsync(cancellationToken);

        return concept is null ? null : new TerminologyLookupResult(
            CanonicalSystem, concept.Id, concept.PreferredTerm ?? concept.Fsn, concept.Version, "SnomedDatabase");
    }

    private static bool IsSnomed(string system) =>
        string.Equals(system?.TrimEnd('/'), CanonicalSystem, StringComparison.OrdinalIgnoreCase)
        || string.Equals(system, "urn:oid:2.16.840.1.113883.6.96", StringComparison.OrdinalIgnoreCase);
}
