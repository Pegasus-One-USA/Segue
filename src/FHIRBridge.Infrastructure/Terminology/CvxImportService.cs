using Microsoft.EntityFrameworkCore;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Imports an uploaded CDC CVX code-set export. Unlike the RF2/RRF-zipped vocabularies, CDC distributes CVX as a
/// plain flat file (not zipped) — this reads the uploaded file directly as delimited text via
/// <see cref="DelimitedTextParser"/>. A full export represents the complete current code set, so
/// terminology.CvxCodes is truncated and reloaded.
/// </summary>
public sealed class CvxImportService : ICvxImportService
{
    private readonly FHIRBridgeDbContext _db;
    public CvxImportService(FHIRBridgeDbContext db) => _db = db;

    public async Task<CvxImportResult> ImportAsync(string filePath, CancellationToken cancellationToken)
    {
        var history = new CvxImportHistory(null, null);
        _db.CvxImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var version = DateTime.UtcNow.ToString("yyyyMMdd");
            history.SetVersion(version);

            await using var stream = File.OpenRead(filePath);
            var (index, rows) = DelimitedTextParser.Read(stream);
            var codes = new List<(string Code, string ShortDescription, string FullVaccineName)>();
            foreach (var row in rows)
            {
                var code = DelimitedTextParser.Get(index, row, "CVX Code", "CVXCode", "Code");
                if (string.IsNullOrWhiteSpace(code)) continue;
                var shortDescription = DelimitedTextParser.Get(index, row, "Short Description", "ShortDescription");
                var fullName = DelimitedTextParser.Get(index, row, "Full Vaccine Name", "FullVaccineName", "Vaccine Name", "Description");
                codes.Add((code, string.IsNullOrWhiteSpace(shortDescription) ? code : shortDescription, string.IsNullOrWhiteSpace(fullName) ? shortDescription : fullName));
            }
            if (codes.Count == 0) throw new InvalidDataException("The CVX release contains no codes.");

            // Dedupe before touching the DbContext — a source file with a repeated code would otherwise try to
            // Add two brand-new CvxCode instances sharing the same key, which EF's change tracker rejects.
            var deduped = codes.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();

            var existing = await _db.CvxCodes.ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in deduped)
            {
                incoming.Add(row.Code);
                if (!existing.TryGetValue(row.Code, out var entity))
                {
                    entity = new CvxCode(row.Code, row.ShortDescription, row.FullVaccineName, true, version);
                    _db.CvxCodes.Add(entity);
                }
                else
                {
                    entity.Update(row.ShortDescription, row.FullVaccineName, version, true);
                }
            }
            foreach (var code in existing.Values.Where(x => !incoming.Contains(x.Code)))
                code.Update(code.ShortDescription, code.FullVaccineName, code.Version, false);

            foreach (var activeVersion in await _db.CvxVersions.Where(x => x.IsActive).ToListAsync(cancellationToken)) activeVersion.SetActive(false);
            _db.CvxVersions.Add(new CvxVersion(version, null, null, true));

            history.Complete(deduped.Count);
            await _db.SaveChangesAsync(cancellationToken);
            return new(version, deduped.Count);
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            history.Fail(exception.ToString());
            _db.CvxImportHistory.Update(history);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
}
