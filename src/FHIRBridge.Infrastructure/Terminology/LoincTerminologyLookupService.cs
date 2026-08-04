using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Reads active LOINC terms from the local terminology schema; it never calls the network.</summary>
public sealed class LoincTerminologyLookupService
{
    public const string CanonicalSystem = "http://loinc.org";
    private readonly FHIRBridgeDbContext _dbContext;

    public LoincTerminologyLookupService(FHIRBridgeDbContext dbContext) => _dbContext = dbContext;

    public async Task<TerminologyLookupResult?> LookupAsync(string system, string code, CancellationToken cancellationToken)
    {
        if (!IsLoinc(system) || string.IsNullOrWhiteSpace(code)) return null;

        var concept = await _dbContext.LoincConcepts.AsNoTracking()
            .Where(x => x.Code == code.Trim() && x.IsActive)
            .Select(x => new { x.Code, x.Display, x.LongCommonName, x.Version })
            .SingleOrDefaultAsync(cancellationToken);

        return concept is null ? null : new TerminologyLookupResult(
            CanonicalSystem, concept.Code, concept.Display ?? concept.LongCommonName, concept.Version, "LoincDatabase");
    }

    private static bool IsLoinc(string system) =>
        string.Equals(system?.TrimEnd('/'), CanonicalSystem, StringComparison.OrdinalIgnoreCase)
        || string.Equals(system, "urn:oid:2.16.840.1.113883.6.1", StringComparison.OrdinalIgnoreCase);
}
