using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Reads code/display data synced locally by the 13 Hapi*TerminologySyncService "Run Now" jobs
/// (see <see cref="Hapi.HapiLocalTerminologyWriter"/>) — one indexed local SQL join, no network call.
/// This replaces the old behavior of resolving these lookups over HTTP against the remote HAPI
/// terminology server, which was the actual cause of the ~120s-per-lookup timeout this class exists
/// to fix.
/// </summary>
public sealed class HapiLocalTerminologyLookupService
{
    private readonly FHIRBridgeDbContext _dbContext;

    public HapiLocalTerminologyLookupService(FHIRBridgeDbContext dbContext) => _dbContext = dbContext;

    public async Task<TerminologyLookupResult?> LookupAsync(string system, string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmedSystem = system.Trim();
        var trimmedCode = code.Trim();

        var result = await (
            from codeSystem in _dbContext.TrmCodeSystems
            where codeSystem.CodeSystemUri == trimmedSystem && codeSystem.CurrentVersionPid != null
            join version in _dbContext.TrmCodeSystemVers on codeSystem.CurrentVersionPid equals version.Pid
            join concept in _dbContext.TrmConcepts on version.Pid equals concept.CodeSystemPid
            where concept.CodeVal == trimmedCode
            select new
            {
                concept.Display,
                concept.ShortDescription,
                concept.LongDescription,
                concept.LongCommonName,
                concept.IsActive,
                version.CsVersionId
            })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        return result is null
            ? null
            : new TerminologyLookupResult(
                trimmedSystem, trimmedCode, result.Display, result.CsVersionId, "HapiLocalTerminologyDatabase",
                result.ShortDescription, result.LongDescription, result.LongCommonName, result.IsActive);
    }

    /// <summary>Searches every synced code system's current version for a matching code, ignoring which
    /// system the caller expected — backs <see cref="CompositeTerminologyLookupService.LookupAnyLocalSystemAsync"/>.
    /// Ordered by <c>CodeSystemUri</c> so a code that happens to collide across two systems resolves
    /// deterministically rather than however the query planner returns rows.</summary>
    public async Task<TerminologyLookupResult?> LookupAnySystemAsync(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmedCode = code.Trim();

        var result = await (
            from codeSystem in _dbContext.TrmCodeSystems
            where codeSystem.CurrentVersionPid != null
            join version in _dbContext.TrmCodeSystemVers on codeSystem.CurrentVersionPid equals version.Pid
            join concept in _dbContext.TrmConcepts on version.Pid equals concept.CodeSystemPid
            where concept.CodeVal == trimmedCode
            orderby codeSystem.CodeSystemUri
            select new
            {
                codeSystem.CodeSystemUri,
                concept.Display,
                concept.ShortDescription,
                concept.LongDescription,
                concept.LongCommonName,
                concept.IsActive,
                version.CsVersionId
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        return result is null
            ? null
            : new TerminologyLookupResult(
                result.CodeSystemUri, trimmedCode, result.Display, result.CsVersionId, "HapiLocalTerminologyDatabase",
                result.ShortDescription, result.LongDescription, result.LongCommonName, result.IsActive);
    }
}
