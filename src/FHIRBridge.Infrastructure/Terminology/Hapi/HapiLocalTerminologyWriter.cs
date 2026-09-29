using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Shared "load this CodeSystem into local storage" step for every Hapi*TerminologySyncService,
/// replacing the old <see cref="HapiTerminologyServerClient.PutCodeSystemAsync"/> HTTP PUT to the
/// remote HAPI server. Writes into <see cref="TrmCodeSystem"/>/<see cref="TrmCodeSystemVer"/>/
/// <see cref="TrmConcept"/> — local MSSQL tables mirroring HAPI's own <c>trm_codesystem</c>/
/// <c>trm_codesystem_ver</c>/<c>trm_concept</c> Postgres schema — so lookups at transform time become
/// an indexed local SQL query instead of a network round-trip.
///
/// A version with no natural release identifier (several of the 13 sources have none) falls back to
/// <see cref="UnversionedVersionId"/>, a fixed placeholder, so re-running "Run Now" reuses the same
/// version row and simply replaces its concepts rather than accumulating a new version every run.
/// </summary>
public sealed class HapiLocalTerminologyWriter
{
    private const string UnversionedVersionId = "unversioned";
    private const int InsertBatchSize = 5_000;

    private readonly FHIRBridgeDbContext _dbContext;
    private readonly ILogger<HapiLocalTerminologyWriter> _logger;

    public HapiLocalTerminologyWriter(FHIRBridgeDbContext dbContext, ILogger<HapiLocalTerminologyWriter> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>Back-compat overload for callers that only have code+display. Every concept is stored
    /// with no extra descriptions and IsActive=true, matching the documented default for a source that
    /// publishes no status signal.</summary>
    public Task WriteConceptsAsync(
        string codeSystemUri,
        string? csName,
        string? version,
        IEnumerable<(string Code, string Display)> concepts,
        CancellationToken cancellationToken) =>
        WriteConceptsAsync(
            codeSystemUri,
            csName,
            version,
            concepts.Select(c => new TerminologyConceptRecord(c.Code, c.Display)),
            cancellationToken);

    public async Task WriteConceptsAsync(
        string codeSystemUri,
        string? csName,
        string? version,
        IEnumerable<TerminologyConceptRecord> concepts,
        CancellationToken cancellationToken)
    {
        var versionId = string.IsNullOrWhiteSpace(version) ? UnversionedVersionId : version.Trim();
        _logger.LogInformation("WriteConceptsAsync starting for {CodeSystemUri} version {VersionId}.", codeSystemUri, versionId);

        var codeSystem = await _dbContext.TrmCodeSystems
            .SingleOrDefaultAsync(x => x.CodeSystemUri == codeSystemUri, cancellationToken);
        if (codeSystem is null)
        {
            codeSystem = new TrmCodeSystem(codeSystemUri, csName);
            _dbContext.TrmCodeSystems.Add(codeSystem);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            codeSystem.Rename(csName);
        }
        _logger.LogInformation("WriteConceptsAsync: TrmCodeSystem row ready for {CodeSystemUri} (Pid {Pid}).", codeSystemUri, codeSystem.Pid);

        var codeSystemVer = await _dbContext.TrmCodeSystemVers
            .SingleOrDefaultAsync(x => x.CodeSystemPid == codeSystem.Pid && x.CsVersionId == versionId, cancellationToken);
        if (codeSystemVer is null)
        {
            codeSystemVer = new TrmCodeSystemVer(codeSystem.Pid, versionId, csName);
            _dbContext.TrmCodeSystemVers.Add(codeSystemVer);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        _logger.LogInformation("WriteConceptsAsync: TrmCodeSystemVer row ready for {CodeSystemUri} (VerPid {VerPid}).", codeSystemUri, codeSystemVer.Pid);

        codeSystem.SetCurrentVersion(codeSystemVer.Pid);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // A re-sync of the same version naturally supersedes its prior concepts — delete, then
        // (re)insert in batches so a 400k-concept system (SNOMED/RxNorm) doesn't balloon the change
        // tracker's memory or slow SaveChanges down to a crawl. Batched load+RemoveRange rather than
        // ExecuteDeleteAsync so this also works against the in-memory provider used in unit tests.
        // EF Core detaches deleted entities automatically after a successful SaveChanges, so no manual
        // detach is needed here — unlike the insert batches below (see FlushBatchAsync's remarks).
        var existingPids = await _dbContext.TrmConcepts
            .Where(x => x.CodeSystemPid == codeSystemVer.Pid)
            .Select(x => x.Pid)
            .ToListAsync(cancellationToken);
        _logger.LogInformation("WriteConceptsAsync: deleting {ExistingCount} prior concepts for {CodeSystemUri}.", existingPids.Count, codeSystemUri);
        var deletedSoFar = 0;
        foreach (var pidBatch in existingPids.Chunk(InsertBatchSize))
        {
            var toDelete = await _dbContext.TrmConcepts
                .Where(x => pidBatch.Contains(x.Pid))
                .ToListAsync(cancellationToken);
            _dbContext.TrmConcepts.RemoveRange(toDelete);
            await _dbContext.SaveChangesAsync(cancellationToken);
            deletedSoFar += toDelete.Count;
            _logger.LogInformation("WriteConceptsAsync: deleted {DeletedSoFar}/{ExistingCount} prior concepts for {CodeSystemUri}.", deletedSoFar, existingPids.Count, codeSystemUri);
        }

        var batch = new List<TrmConcept>(InsertBatchSize);
        var insertedSoFar = 0;
        foreach (var concept in concepts)
        {
            batch.Add(new TrmConcept(
                codeSystemVer.Pid,
                concept.Code,
                concept.Display,
                concept.ShortDescription,
                concept.LongDescription,
                concept.LongCommonName,
                concept.IsActive));
            if (batch.Count < InsertBatchSize)
            {
                continue;
            }

            insertedSoFar += batch.Count;
            await FlushBatchAsync(batch, cancellationToken);
            _logger.LogInformation("WriteConceptsAsync: inserted {InsertedSoFar} concepts so far for {CodeSystemUri}.", insertedSoFar, codeSystemUri);
        }

        insertedSoFar += batch.Count;
        await FlushBatchAsync(batch, cancellationToken);
        _logger.LogInformation("WriteConceptsAsync finished for {CodeSystemUri}: {InsertedSoFar} concepts written.", codeSystemUri, insertedSoFar);
    }

    private async Task FlushBatchAsync(List<TrmConcept> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        _dbContext.TrmConcepts.AddRange(batch);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Detach only this batch's own entities to bound the change tracker's memory for a
        // 400k-concept system — NOT ChangeTracker.Clear(), which would also detach whatever else the
        // caller's shared DbContext is tracking (e.g. HapiTerminologyConfigurationService's in-flight
        // HapiTerminologyImportHistory row), silently dropping its later history.Complete(...) write.
        foreach (var concept in batch)
        {
            _dbContext.Entry(concept).State = EntityState.Detached;
        }

        batch.Clear();
    }
}
