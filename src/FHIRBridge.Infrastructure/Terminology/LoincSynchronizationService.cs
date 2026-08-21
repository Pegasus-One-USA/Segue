using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Imports an authenticated, checksum-verified LOINC release into the local terminology schema.</summary>
public sealed class LoincSynchronizationService : ILoincSynchronizationService
{
    private const string LoincSystem = "http://loinc.org";
    private readonly ILoincReleaseClient _client;
    private readonly FHIRBridgeDbContext _db;
    public LoincSynchronizationService(ILoincReleaseClient client, FHIRBridgeDbContext db) => (_client, _db) = (client, db);

    public async Task<LoincSynchronizationResult> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var release = await _client.GetCurrentReleaseAsync(cancellationToken);
        if (await _db.LoincVersions.AnyAsync(x => x.Version == release.Version && x.IsActive, cancellationToken))
            return new(release.Version, await _db.LoincConcepts.CountAsync(x => x.IsActive, cancellationToken), true);

        var history = new LoincImportHistory(release.Version, release.Md5Hash);
        _db.LoincImportHistory.Add(history); await _db.SaveChangesAsync(cancellationToken);
        try
        {
            var zipPath = await _client.DownloadReleaseAsync(release, cancellationToken);
            using var archive = ZipFile.OpenRead(zipPath);
            var rows = ReadLoincRows(archive).ToList();
            if (rows.Count == 0) throw new InvalidDataException("The release contains no LOINC terms.");
            if (rows.Select(x => x.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count) throw new InvalidDataException("The release contains duplicate LOINC codes.");

            var conceptMapRows = ReadMapToRows(archive).ToList();
            var answerListRows = ReadAnswerListRows(archive).ToList();
            var groupRows = ReadGroupRows(archive).ToList();

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var existing = await _db.LoincConcepts.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                incoming.Add(row.Code);
                if (!existing.TryGetValue(row.Code, out var concept))
                {
                    concept = new LoincConcept(row.Code, row.Display, row.LongName, release.Version, row.IsActive);
                    _db.LoincConcepts.Add(concept);
                }
                concept.Update(row.Display, row.LongName, row.Class, row.Component, row.Property, row.TimeAspect, row.System, row.Scale, row.Method, row.Status, release.Version, row.IsActive);
            }
            foreach (var concept in existing.Values.Where(x => !incoming.Contains(x.Code))) concept.Deactivate();
            foreach (var version in await _db.LoincVersions.Where(x => x.IsActive).ToListAsync(cancellationToken)) version.SetActive(false);
            _db.LoincVersions.Add(new LoincVersion(release.Version, release.ReleaseDateUtc, release.Md5Hash, true));

            foreach (var map in conceptMapRows)
                _db.LoincConceptMaps.Add(new LoincConceptMap(LoincSystem, map.FromLoinc, LoincSystem, map.ToLoinc, map.Equivalence, map.ToDisplay, release.Version));
            foreach (var answer in answerListRows)
                _db.LoincAnswerLists.Add(new LoincAnswerList(answer.AnswerListId, answer.AnswerListName, null, answer.AnswerCode, answer.AnswerDisplay, release.Version));
            foreach (var group in groupRows)
                _db.LoincGroups.Add(new LoincGroup(group.GroupId, group.GroupName, group.ParentGroupId, release.Version));

            history.Complete(rows.Count);
            await _db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return new(release.Version, rows.Count, false);
        }
        catch (Exception exception)
        {
            history.Fail(exception.Message); await _db.SaveChangesAsync(CancellationToken.None); throw;
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string suffix) =>
        archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static (Dictionary<string, int> Index, List<string[]> Rows) ReadCsv(Stream stream)
    {
        using var parser = new TextFieldParser(stream) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidDataException("CSV has no header.");
        var index = headers.Select((name, i) => (name, i)).ToDictionary(x => x.name.Trim(), x => x.i, StringComparer.OrdinalIgnoreCase);
        var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields() ?? Array.Empty<string>());
        return (index, rows);
    }

    private static string Get(Dictionary<string, int> index, string[] row, params string[] fields)
    {
        foreach (var field in fields) if (index.TryGetValue(field, out var i) && i < row.Length) return row[i]?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private static IEnumerable<Row> ReadLoincRows(ZipArchive archive)
    {
        var entry = FindEntry(archive, "LoincTable/Loinc.csv") ?? throw new InvalidDataException("LoincTable/Loinc.csv was not found in the release archive.");
        using var stream = entry.Open();
        var (index, rows) = ReadCsv(stream);
        foreach (var current in rows)
        {
            var code = Get(index, current, "LOINC_NUM"); if (string.IsNullOrWhiteSpace(code)) continue;
            var status = Get(index, current, "STATUS");
            yield return new(code, Get(index, current, "SHORTNAME", "LONG_COMMON_NAME"), Get(index, current, "LONG_COMMON_NAME"), Get(index, current, "CLASS"),
                Get(index, current, "COMPONENT"), Get(index, current, "PROPERTY"), Get(index, current, "TIME_ASPCT"), Get(index, current, "SYSTEM"),
                Get(index, current, "SCALE_TYP"), Get(index, current, "METHOD_TYP"), status, !string.Equals(status, "DEPRECATED", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Parses the optional LOINC-to-LOINC "map to" relationships that back the $translate operation. Absent in some release editions.</summary>
    private static IEnumerable<MapToRow> ReadMapToRows(ZipArchive archive)
    {
        var entry = FindEntry(archive, "AccessoryFiles/MapTo/MapTo.csv");
        if (entry is null) yield break;
        using var stream = entry.Open();
        var (index, rows) = ReadCsv(stream);
        foreach (var current in rows)
        {
            var fromLoinc = Get(index, current, "FROM_LOINC", "LOINC_FROM", "FROM_LOINC_NUM");
            var toLoinc = Get(index, current, "TO_LOINC", "LOINC_TO", "TO_LOINC_NUM");
            if (string.IsNullOrWhiteSpace(fromLoinc) || string.IsNullOrWhiteSpace(toLoinc)) continue;
            yield return new(fromLoinc, toLoinc, Get(index, current, "TO_LONG_COMMON_NAME", "TO_DISPLAY", "TO_LCN"), Get(index, current, "EQUIVALENCE", "MAP_TO_REASON", "COMMENT"));
        }
    }

    /// <summary>Parses the optional LOINC answer list catalog used for coded-answer expansion. Absent in some release editions.</summary>
    private static IEnumerable<AnswerListRow> ReadAnswerListRows(ZipArchive archive)
    {
        var entry = FindEntry(archive, "AccessoryFiles/AnswerFile/AnswerList.csv");
        if (entry is null) yield break;
        using var stream = entry.Open();
        var (index, rows) = ReadCsv(stream);
        foreach (var current in rows)
        {
            var answerListId = Get(index, current, "AnswerListId", "ANSWER_LIST_ID");
            if (string.IsNullOrWhiteSpace(answerListId)) continue;
            yield return new(answerListId, Get(index, current, "AnswerListName", "ANSWER_LIST_NAME"),
                Get(index, current, "AnswerStringId", "LocalAnswerCode", "ANSWER_CODE", "AnswerCode"),
                Get(index, current, "DisplayText", "AnswerText", "ANSWER_DISPLAY", "AnswerDisplay"));
        }
    }

    /// <summary>Parses the optional LOINC group hierarchy used for group-based expansion. Absent in some release editions.</summary>
    private static IEnumerable<GroupRow> ReadGroupRows(ZipArchive archive)
    {
        var groupEntry = FindEntry(archive, "AccessoryFiles/GroupFile/Group.csv");
        if (groupEntry is null) yield break;

        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parentEntry = FindEntry(archive, "AccessoryFiles/GroupFile/ParentGroupGroup.csv") ?? FindEntry(archive, "AccessoryFiles/GroupFile/ParentGroup.csv");
        if (parentEntry is not null)
        {
            using var parentStream = parentEntry.Open();
            var (parentIndex, parentRows) = ReadCsv(parentStream);
            foreach (var current in parentRows)
            {
                var groupId = Get(parentIndex, current, "GroupId", "GROUP_ID");
                var parentGroupId = Get(parentIndex, current, "ParentGroupId", "PARENT_GROUP_ID");
                if (!string.IsNullOrWhiteSpace(groupId) && !string.IsNullOrWhiteSpace(parentGroupId)) parents[groupId] = parentGroupId;
            }
        }

        using var stream = groupEntry.Open();
        var (index, rows) = ReadCsv(stream);
        foreach (var current in rows)
        {
            var groupId = Get(index, current, "GroupId", "GROUP_ID");
            if (string.IsNullOrWhiteSpace(groupId)) continue;
            yield return new(groupId, Get(index, current, "GroupName", "GROUP_NAME"), parents.GetValueOrDefault(groupId));
        }
    }

    private sealed record Row(string Code, string Display, string LongName, string Class, string Component, string Property, string TimeAspect, string System, string Scale, string Method, string Status, bool IsActive);
    private sealed record MapToRow(string FromLoinc, string ToLoinc, string ToDisplay, string Equivalence);
    private sealed record AnswerListRow(string AnswerListId, string AnswerListName, string AnswerCode, string AnswerDisplay);
    private sealed record GroupRow(string GroupId, string GroupName, string? ParentGroupId);
}
