using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Imports an uploaded CMS ICD-10-PCS "Order File" release archive into the local terminology schema.
///
/// Confirmed by downloading and inspecting the real April 2026 release: this is a fixed-width record file with
/// NO header row and NO delimiter (same finding as HCPCS's release — see <see cref="HcpcsImportService"/>).
/// Field positions (1-based), per CMS's own <c>icd10pcsOrderFile.pdf</c> layout doc bundled in the release zip:
/// Order Number = 1-5, Code = 7-13, HIPAA-valid-code flag = 15 ('1' = a real billable code; '0' = a section
/// header row with a 3-character category code, not a leaf code — skipped), Short Description = 17-76 (60
/// chars), Long Description = 78 to end of line. The release zip also bundles a mid-year addenda file and a PDF
/// of this same layout doc — the data file is identified by name containing "icd10pcs_order", not just "the
/// first .txt found".
///
/// A full release represents the complete current code set, so terminology.Icd10PcsCodes is truncated and reloaded.
/// </summary>
public sealed class Icd10PcsImportService : IIcd10PcsImportService
{
    private const int CodeStart = 6;
    private const int CodeLength = 7;
    private const int HipaaFlagPosition = 14;
    private const int ShortDescriptionStart = 16;
    private const int ShortDescriptionLength = 60;
    private const int LongDescriptionStart = 77;
    private const char ValidCodeFlag = '1';

    private readonly FHIRBridgeDbContext _db;
    public Icd10PcsImportService(FHIRBridgeDbContext db) => _db = db;

    public async Task<Icd10PcsImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken)
    {
        var history = new Icd10PcsImportHistory(null, null);
        _db.Icd10PcsImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var entry = archive.Entries.FirstOrDefault(x =>
                    x.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    && x.FullName.Contains("icd10pcs_order", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("No icd10pcs_order file was found in the ICD-10-PCS release archive.");

            var version = DateTime.UtcNow.ToString("yyyyMMdd");
            history.SetVersion(version);

            var codes = new List<(string Code, string ShortDescription, string LongDescription)>();
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length <= LongDescriptionStart) continue;
                    if (line[HipaaFlagPosition] != ValidCodeFlag) continue;

                    var code = line.Substring(CodeStart, CodeLength).Trim();
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    var shortDescription = line.Substring(ShortDescriptionStart, ShortDescriptionLength).Trim();
                    var longDescription = line[LongDescriptionStart..].Trim();
                    codes.Add((code, string.IsNullOrWhiteSpace(shortDescription) ? code : shortDescription, string.IsNullOrWhiteSpace(longDescription) ? shortDescription : longDescription));
                }
            }
            if (codes.Count == 0) throw new InvalidDataException("The ICD-10-PCS release contains no codes.");

            // Dedupe before touching the DbContext — a source file with a repeated code would otherwise try to
            // Add two brand-new Icd10PcsCode instances sharing the same key, which EF's change tracker rejects.
            var deduped = codes.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();

            var existing = await _db.Icd10PcsCodes.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in deduped)
            {
                incoming.Add(row.Code);
                if (!existing.TryGetValue(row.Code, out var entity))
                {
                    entity = new Icd10PcsCode(row.Code, row.ShortDescription, row.LongDescription, true, version);
                    _db.Icd10PcsCodes.Add(entity);
                }
                else
                {
                    entity.Update(row.ShortDescription, row.LongDescription, version, true);
                }
            }
            foreach (var code in existing.Values.Where(x => !incoming.Contains(x.Code)))
                code.Update(code.ShortDescription, code.LongDescription, code.Version, false);

            foreach (var activeVersion in await _db.Icd10PcsVersions.Where(x => x.IsActive).ToListAsync(cancellationToken)) activeVersion.SetActive(false);
            _db.Icd10PcsVersions.Add(new Icd10PcsVersion(version, null, null, true));

            history.Complete(deduped.Count);
            await _db.SaveChangesAsync(cancellationToken);
            return new(version, deduped.Count);
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            history.Fail(exception.ToString());
            _db.Icd10PcsImportHistory.Update(history);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (File.Exists(zipFilePath)) File.Delete(zipFilePath);
        }
    }
}
