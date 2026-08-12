using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Reads active ICD-10-CM codes from the local terminology schema; it never calls the network.</summary>
public sealed class Icd10TerminologyLookupService
{
    public const string CanonicalSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    private readonly FHIRBridgeDbContext _dbContext;

    public Icd10TerminologyLookupService(FHIRBridgeDbContext dbContext) => _dbContext = dbContext;

    public async Task<TerminologyLookupResult?> LookupAsync(string system, string code, CancellationToken cancellationToken)
    {
        if (!IsIcd10(system) || string.IsNullOrWhiteSpace(code)) return null;

        // CMS's official ICD-10-CM "order file" (what Icd10ImportService parses) stores codes WITHOUT the
        // decimal point (e.g. "J181"), while every clinical/FHIR source sends the dotted clinical convention
        // ("J18.1") — a plain string match between the two conventions always misses. Stripping "." from both
        // sides before comparing matches regardless of which convention either side happens to use.
        var normalizedCode = code.Trim().Replace(".", "", StringComparison.Ordinal);

        var entry = await _dbContext.Icd10Codes.AsNoTracking()
            .Where(x => x.Code.Replace(".", "") == normalizedCode && x.IsActive)
            .Select(x => new { x.Code, x.ShortDescription, x.LongDescription, x.Version })
            .FirstOrDefaultAsync(cancellationToken);

        return entry is null ? null : new TerminologyLookupResult(
            CanonicalSystem, entry.Code, entry.LongDescription ?? entry.ShortDescription, entry.Version, "Icd10Database");
    }

    private static bool IsIcd10(string system) =>
        string.Equals(system?.TrimEnd('/'), CanonicalSystem, StringComparison.OrdinalIgnoreCase)
        || string.Equals(system, "urn:oid:2.16.840.1.113883.6.90", StringComparison.OrdinalIgnoreCase);
}
