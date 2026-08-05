using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Imports an uploaded RxNorm "Full Monthly Release" archive into the local terminology schema. Only
/// RXNCONSO.RRF (concepts/atoms/strings) is used for this v1 slice — RXNREL.RRF (relationships) and
/// RXNSAT.RRF (attributes) are not yet imported. RXNCONSO has many rows per RXCUI (one per source
/// atom/string); a single streaming pass picks one canonical name per concept (preferring an RXNORM-sourced,
/// ISPREF='Y' atom) without materializing every row, then the whole terminology.RxNormConcepts table is
/// truncated and bulk-copied — like ICD-10-CM, a full release represents the complete current code set, so
/// no upsert-against-existing logic is needed.
/// </summary>
public sealed class RxNormImportService : IRxNormImportService
{
    private readonly FHIRBridgeDbContext _db;
    public RxNormImportService(FHIRBridgeDbContext db) => _db = db;

    public async Task<RxNormImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken)
    {
        var history = new RxNormImportHistory(null, null);
        _db.RxNormImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);

        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var consoEntry = FindConsoEntry(archive) ?? throw new InvalidDataException("No RXNCONSO.RRF file was found in the release archive.");
            var version = ExtractVersion(archive, consoEntry);
            history.SetVersion(version);

            var canonical = BuildCanonicalConcepts(consoEntry);
            if (canonical.Count == 0) throw new InvalidDataException("The release contains no RxNorm concepts.");

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var connection = (SqlConnection)_db.Database.GetDbConnection();
            var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();

            await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE terminology.RxNormConcepts", cancellationToken);
            await TerminologyBulkCopy.WriteAsync(connection, sqlTransaction, "terminology.RxNormConcepts",
                ["Rxcui", "Name", "TermType", "IsActive", "Version", "CreatedOnUtc", "ModifiedOnUtc"],
                canonical.Select(pair => new object?[] { pair.Key, pair.Value.Name, pair.Value.TermType, pair.Value.IsActive, version, DateTime.UtcNow, DateTime.UtcNow }),
                cancellationToken);

            foreach (var activeVersion in await _db.RxNormVersions.Where(x => x.IsActive).ToListAsync(cancellationToken)) activeVersion.SetActive(false);
            var existingVersion = await _db.RxNormVersions.FirstOrDefaultAsync(x => x.Version == version, cancellationToken);
            if (existingVersion is not null) existingVersion.SetActive(true);
            else _db.RxNormVersions.Add(new RxNormVersion(version, null, null, true));

            history.Complete(canonical.Count);
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new(version, canonical.Count);
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            history.Fail(exception.ToString());
            _db.RxNormImportHistory.Update(history);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            File.Delete(zipFilePath);
        }
    }

    private static ZipArchiveEntry? FindConsoEntry(ZipArchive archive) =>
        archive.Entries.FirstOrDefault(x => x.FullName.EndsWith("RXNCONSO.RRF", StringComparison.OrdinalIgnoreCase));

    /// <summary>RxNorm release folders/zips embed the release date (commonly MMDDYYYY) rather than a
    /// well-known single format, so this only surfaces it as an opaque label — it is never parsed into a
    /// date — falling back to today's date if the archive doesn't carry one at all.</summary>
    private static string ExtractVersion(ZipArchive archive, ZipArchiveEntry consoEntry)
    {
        var match = Regex.Match(consoEntry.FullName, @"(\d{8})");
        if (!match.Success) match = Regex.Match(archive.Entries.FirstOrDefault()?.FullName ?? string.Empty, @"(\d{8})");
        return match.Success ? match.Groups[1].Value : DateTime.UtcNow.ToString("yyyyMMdd");
    }

    /// <summary>
    /// Single streaming pass over RXNCONSO.RRF (pipe-delimited, no header, fixed column order per the RxNorm
    /// technical documentation): RXCUI(0), LAT(1), TS(2), LUI(3), STT(4), SUI(5), ISPREF(6), RXAUI(7),
    /// SAUI(8), SCUI(9), SDUI(10), SAB(11), TTY(12), CODE(13), STR(14), SRL(15), SUPPRESS(16), CVF(17).
    /// Keeps only the best candidate row seen so far per RXCUI (RXNORM-sourced and ISPREF='Y' wins), so
    /// memory is bounded by distinct-concept count (~300-500K), not the full row count (several million).
    /// </summary>
    private static Dictionary<string, ConceptCandidate> BuildCanonicalConcepts(ZipArchiveEntry entry)
    {
        var candidates = new Dictionary<string, ConceptCandidate>();
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var row = line.Split('|');
            if (row.Length < 17) continue;

            var rxcui = row[0];
            if (string.IsNullOrWhiteSpace(rxcui)) continue;

            var candidate = new ConceptCandidate(
                Name: row[14],
                TermType: row[12],
                IsActive: row[16] == "N",
                IsRxNormSource: row[11].Equals("RXNORM", StringComparison.OrdinalIgnoreCase),
                IsPreferred: row[6].Equals("Y", StringComparison.OrdinalIgnoreCase));

            if (!candidates.TryGetValue(rxcui, out var existing) || IsBetterCandidate(candidate, existing))
                candidates[rxcui] = candidate;
        }
        return candidates;
    }

    private static bool IsBetterCandidate(ConceptCandidate candidate, ConceptCandidate existing)
    {
        if (candidate.IsRxNormSource != existing.IsRxNormSource) return candidate.IsRxNormSource;
        if (candidate.IsPreferred != existing.IsPreferred) return candidate.IsPreferred;
        return false;
    }

    private sealed record ConceptCandidate(string Name, string TermType, bool IsActive, bool IsRxNormSource, bool IsPreferred);
}
