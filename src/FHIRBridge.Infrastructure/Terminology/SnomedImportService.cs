using System.Globalization;
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
/// Imports an uploaded SNOMED CT RF2 Snapshot release archive into the local terminology schema.
/// Concepts (which need upsert-against-existing logic) are bulk-copied into a staging table and merged;
/// Descriptions/Relationships (which are wholesale-replaced every import) are truncated and bulk-copied
/// directly. RF2 is simple tab-delimited text with no quoting, so a manual split is used instead of
/// TextFieldParser — both choices avoid the row-by-row EF Core change-tracking cost that made the
/// original implementation too slow/memory-hungry for the full International Edition (~5.8M rows).
/// </summary>
public sealed class SnomedImportService : ISnomedImportService
{
    private const string FullySpecifiedNameTypeId = "900000000000003001";
    private const string SynonymTypeId = "900000000000013009";

    private readonly FHIRBridgeDbContext _db;
    public SnomedImportService(FHIRBridgeDbContext db) => _db = db;

    public async Task<SnomedImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken)
    {
        var history = new SnomedImportHistory(null, null);
        _db.SnomedImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);

        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var conceptEntry = FindEntry(archive, "_Concept_Snapshot") ?? throw new InvalidDataException("No Concept Snapshot file was found in the release archive.");
            var version = ExtractVersion(conceptEntry.Name);
            history.SetVersion(version);

            // Only the FSN/preferred-term text per concept is kept in memory — not the full description
            // rows — so this stays small (one entry per concept) regardless of how many descriptions exist.
            var fsnByConcept = new Dictionary<string, string>();
            var ptByConcept = new Dictionary<string, string>();
            foreach (var description in ReadDescriptionRows(archive))
            {
                if (!description.Active) continue;
                if (description.TypeId == FullySpecifiedNameTypeId) fsnByConcept.TryAdd(description.ConceptId, description.Term);
                else if (description.TypeId == SynonymTypeId) ptByConcept.TryAdd(description.ConceptId, description.Term);
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var connection = (SqlConnection)_db.Database.GetDbConnection();
            var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();

            await _db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS #SnomedConceptStaging", cancellationToken);
            await _db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE #SnomedConceptStaging (
                    Id nvarchar(18) NOT NULL, EffectiveTime date NOT NULL, Active bit NOT NULL,
                    ModuleId nvarchar(18) NOT NULL, DefinitionStatusId nvarchar(18) NOT NULL,
                    Fsn nvarchar(1000) NULL, PreferredTerm nvarchar(1000) NULL, Version nvarchar(32) NOT NULL)
                """, cancellationToken);

            var conceptCount = 0;
            IEnumerable<object?[]> StageConceptRows()
            {
                foreach (var row in ReadConceptRows(conceptEntry))
                {
                    conceptCount++;
                    fsnByConcept.TryGetValue(row.Id, out var fsn);
                    ptByConcept.TryGetValue(row.Id, out var preferredTerm);
                    yield return new object?[] { row.Id, row.EffectiveTime.ToDateTime(TimeOnly.MinValue), row.Active, row.ModuleId, row.DefinitionStatusId, fsn, preferredTerm, version };
                }
            }

            await TerminologyBulkCopy.WriteAsync(connection, sqlTransaction, "#SnomedConceptStaging",
                ["Id", "EffectiveTime", "Active", "ModuleId", "DefinitionStatusId", "Fsn", "PreferredTerm", "Version"],
                StageConceptRows(), cancellationToken);

            if (conceptCount == 0) throw new InvalidDataException("The release contains no SNOMED CT concepts.");

            await _db.Database.ExecuteSqlRawAsync(
                """
                MERGE terminology.SnomedConcepts AS target
                USING #SnomedConceptStaging AS source ON target.Id = source.Id
                WHEN MATCHED THEN UPDATE SET
                    EffectiveTime = source.EffectiveTime, Active = source.Active, ModuleId = source.ModuleId,
                    DefinitionStatusId = source.DefinitionStatusId, Fsn = source.Fsn, PreferredTerm = source.PreferredTerm,
                    Version = source.Version, ModifiedOnUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (Id, EffectiveTime, Active, ModuleId, DefinitionStatusId, Fsn, PreferredTerm, Version, CreatedOnUtc, ModifiedOnUtc)
                VALUES (source.Id, source.EffectiveTime, source.Active, source.ModuleId, source.DefinitionStatusId, source.Fsn, source.PreferredTerm, source.Version, SYSUTCDATETIME(), SYSUTCDATETIME());
                """, cancellationToken);

            await _db.Database.ExecuteSqlRawAsync(
                """
                UPDATE terminology.SnomedConcepts SET Active = 0, ModifiedOnUtc = SYSUTCDATETIME()
                WHERE Active = 1 AND NOT EXISTS (SELECT 1 FROM #SnomedConceptStaging s WHERE s.Id = terminology.SnomedConcepts.Id)
                """, cancellationToken);

            await _db.Database.ExecuteSqlRawAsync("DROP TABLE #SnomedConceptStaging", cancellationToken);

            foreach (var activeVersion in await _db.SnomedVersions.Where(x => x.IsActive).ToListAsync(cancellationToken)) activeVersion.SetActive(false);
            var existingVersion = await _db.SnomedVersions.FirstOrDefaultAsync(x => x.Version == version, cancellationToken);
            if (existingVersion is not null) existingVersion.SetActive(true);
            else _db.SnomedVersions.Add(new SnomedVersion(version, ParseEffectiveTime(version), null, true));

            await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE terminology.SnomedDescriptions", cancellationToken);
            await TerminologyBulkCopy.WriteAsync(connection, sqlTransaction, "terminology.SnomedDescriptions",
                ["Id", "ConceptId", "Term", "TypeId", "LanguageCode", "CaseSignificanceId", "Active", "Version"],
                ReadDescriptionRows(archive).Select(d => new object?[] { d.Id, d.ConceptId, d.Term, d.TypeId, d.LanguageCode, d.CaseSignificanceId, d.Active, version }),
                cancellationToken);

            await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE terminology.SnomedRelationships", cancellationToken);
            await TerminologyBulkCopy.WriteAsync(connection, sqlTransaction, "terminology.SnomedRelationships",
                ["Id", "SourceId", "DestinationId", "TypeId", "RelationshipGroup", "CharacteristicTypeId", "Active", "Version"],
                ReadRelationshipRows(archive).Select(r => new object?[] { r.Id, r.SourceId, r.DestinationId, r.TypeId, r.RelationshipGroup, r.CharacteristicTypeId, r.Active, version }),
                cancellationToken);

            history.Complete(conceptCount);
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new(version, conceptCount);
        }
        catch (Exception exception)
        {
            // Whatever else failed may still be attached (e.g. a duplicate-key SnomedVersion insert) — clear
            // the tracker first and re-attach only `history`, or this recovery save retries the same poisoned
            // graph, fails again, and the real error never makes it into SnomedImportHistory.
            _db.ChangeTracker.Clear();
            history.Fail(exception.ToString());
            _db.SnomedImportHistory.Update(history);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            File.Delete(zipFilePath);
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, params string[] mustContain) =>
        archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && mustContain.All(part => x.FullName.Contains(part, StringComparison.OrdinalIgnoreCase)));

    private static string ExtractVersion(string fileName)
    {
        var match = Regex.Match(fileName, @"(\d{8})");
        if (!match.Success) throw new InvalidDataException("Could not determine the release version from the RF2 file name " + fileName + ".");
        return match.Groups[1].Value;
    }

    private static DateTime? ParseEffectiveTime(string version) =>
        DateTime.TryParseExact(version, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static DateOnly ParseEffectiveDate(string value) => DateOnly.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Get(Dictionary<string, int> index, string[] row, string field) => index.TryGetValue(field, out var i) && i < row.Length ? row[i] : string.Empty;

    private static Dictionary<string, int> BuildIndex(string headerLine) =>
        headerLine.Split('\t').Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<ConceptRow> ReadConceptRows(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var index = BuildIndex(reader.ReadLine() ?? throw new InvalidDataException("RF2 file has no header."));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var row = line.Split('\t');
            var id = Get(index, row, "id"); if (string.IsNullOrWhiteSpace(id)) continue;
            yield return new(id, ParseEffectiveDate(Get(index, row, "effectiveTime")), Get(index, row, "active") == "1",
                Get(index, row, "moduleId"), Get(index, row, "definitionStatusId"));
        }
    }

    private static IEnumerable<DescriptionRow> ReadDescriptionRows(ZipArchive archive)
    {
        var entry = FindEntry(archive, "_Description_Snapshot");
        if (entry is null) yield break;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var index = BuildIndex(reader.ReadLine() ?? throw new InvalidDataException("RF2 file has no header."));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var row = line.Split('\t');
            var id = Get(index, row, "id"); if (string.IsNullOrWhiteSpace(id)) continue;
            yield return new(id, Get(index, row, "conceptId"), Get(index, row, "term"), Get(index, row, "typeId"),
                Get(index, row, "languageCode"), Get(index, row, "caseSignificanceId"), Get(index, row, "active") == "1");
        }
    }

    private static IEnumerable<RelationshipRow> ReadRelationshipRows(ZipArchive archive)
    {
        var entry = FindEntry(archive, "_Relationship_Snapshot");
        if (entry is null) yield break;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var index = BuildIndex(reader.ReadLine() ?? throw new InvalidDataException("RF2 file has no header."));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var row = line.Split('\t');
            var id = Get(index, row, "id"); if (string.IsNullOrWhiteSpace(id)) continue;
            yield return new(id, Get(index, row, "sourceId"), Get(index, row, "destinationId"), Get(index, row, "typeId"),
                int.TryParse(Get(index, row, "relationshipGroup"), out var group) ? group : 0,
                Get(index, row, "characteristicTypeId"), Get(index, row, "active") == "1");
        }
    }

    private sealed record ConceptRow(string Id, DateOnly EffectiveTime, bool Active, string ModuleId, string DefinitionStatusId);
    private sealed record DescriptionRow(string Id, string ConceptId, string Term, string TypeId, string LanguageCode, string CaseSignificanceId, bool Active);
    private sealed record RelationshipRow(string Id, string SourceId, string DestinationId, string TypeId, int RelationshipGroup, string CharacteristicTypeId, bool Active);
}
