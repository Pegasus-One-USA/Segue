using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Imports an uploaded CMS HCPCS Level II release archive into the local terminology schema.
///
/// Confirmed by downloading and inspecting the real July 2026 release (per its own
/// <c>HCPC2026_recordlayout.txt</c>): this is a fixed-width record file with NO header row and NO delimiter —
/// not the tab/pipe/comma format <see cref="DelimitedTextParser"/> was originally written for. Field positions
/// (1-based, per the CMS record layout): Code = 1-5, Record Identification Code (RIC) = 11, Long Description =
/// 12-91, Short Description = 92-119. RIC disambiguates what the record actually represents (a COBOL-style
/// REDEFINES): 3 = first line of a procedure/supply record (the one with a real 5-char code and both
/// descriptions), 4 = a continuation description line for the same record (no valid code), 7/8 = first/continuation
/// lines of a 2-character *modifier* record (e.g. "A1", "AA") using a different sub-field, not a procedure code.
/// Only RIC 3 rows are imported — 4/7/8 are skipped as not representing a standalone procedure code.
///
/// The release zip also bundles several unrelated files (an Excel copy of the same data, a corrections/transaction
/// report, NOC codes, and free-text processing notes) — the data file is identified by containing "ANWEB" in its
/// name while excluding "recordlayout", not just "the first .txt found".
///
/// A full release represents the complete current code set, so terminology.HcpcsCodes is truncated and reloaded.
/// </summary>
public sealed class HcpcsImportService : IHcpcsImportService
{
    private const int CodeStart = 0;
    private const int CodeLength = 5;
    private const int RicPosition = 10;
    private const int LongDescriptionStart = 11;
    private const int LongDescriptionLength = 80;
    private const int ShortDescriptionStart = 91;
    private const int ShortDescriptionLength = 28;
    private const char ProcedureRecordIdentificationCode = '3';

    private readonly FHIRBridgeDbContext _db;
    public HcpcsImportService(FHIRBridgeDbContext db) => _db = db;

    public async Task<HcpcsImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken)
    {
        var history = new HcpcsImportHistory(null, null);
        _db.HcpcsImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var entry = archive.Entries.FirstOrDefault(x =>
                    x.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    && x.FullName.Contains("ANWEB", StringComparison.OrdinalIgnoreCase)
                    && !x.FullName.Contains("recordlayout", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("No ANWEB code file was found in the HCPCS release archive.");

            var version = DateTime.UtcNow.ToString("yyyyMMdd");
            history.SetVersion(version);

            var codes = new List<(string Code, string ShortDescription, string LongDescription)>();
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < ShortDescriptionStart + ShortDescriptionLength) continue;
                    if (line[RicPosition] != ProcedureRecordIdentificationCode) continue;

                    var code = line.Substring(CodeStart, CodeLength).Trim();
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    var longDescription = line.Substring(LongDescriptionStart, LongDescriptionLength).Trim();
                    var shortDescription = line.Substring(ShortDescriptionStart, ShortDescriptionLength).Trim();
                    codes.Add((code, string.IsNullOrWhiteSpace(shortDescription) ? code : shortDescription, string.IsNullOrWhiteSpace(longDescription) ? shortDescription : longDescription));
                }
            }
            if (codes.Count == 0) throw new InvalidDataException("The HCPCS release contains no codes.");

            // Dedupe before touching the DbContext — a source file with a repeated code would otherwise try to
            // Add two brand-new HcpcsCode instances sharing the same key, which EF's change tracker rejects.
            var deduped = codes.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();

            var existing = await _db.HcpcsCodes.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in deduped)
            {
                incoming.Add(row.Code);
                if (!existing.TryGetValue(row.Code, out var entity))
                {
                    entity = new HcpcsCode(row.Code, row.ShortDescription, row.LongDescription, true, version);
                    _db.HcpcsCodes.Add(entity);
                }
                else
                {
                    entity.Update(row.ShortDescription, row.LongDescription, version, true);
                }
            }
            foreach (var code in existing.Values.Where(x => !incoming.Contains(x.Code)))
                code.Update(code.ShortDescription, code.LongDescription, code.Version, false);

            foreach (var activeVersion in await _db.HcpcsVersions.Where(x => x.IsActive).ToListAsync(cancellationToken)) activeVersion.SetActive(false);
            _db.HcpcsVersions.Add(new HcpcsVersion(version, null, null, true));

            history.Complete(deduped.Count);
            await _db.SaveChangesAsync(cancellationToken);
            return new(version, deduped.Count);
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            history.Fail(exception.ToString());
            _db.HcpcsImportHistory.Update(history);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (File.Exists(zipFilePath)) File.Delete(zipFilePath);
        }
    }
}
