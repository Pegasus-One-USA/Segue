using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Imports the UCUM specification's <c>ucum-essence.xml</c> (both <c>&lt;unit&gt;</c> and <c>&lt;base-unit&gt;</c>
/// elements — every base unit is itself a valid UCUM code). A full file represents the complete current unit set,
/// so terminology.UcumUnits is truncated and reloaded.
/// </summary>
public sealed class UcumImportService : IUcumImportService
{
    private readonly FHIRBridgeDbContext _db;
    public UcumImportService(FHIRBridgeDbContext db) => _db = db;

    public async Task<UcumImportResult> ImportAsync(string filePath, CancellationToken cancellationToken)
    {
        var history = new UcumImportHistory(null, null);
        _db.UcumImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var document = XDocument.Load(filePath);
            var root = document.Root ?? throw new InvalidDataException("ucum-essence.xml has no root element.");
            var version = root.Attribute("version")?.Value ?? root.Attribute("revision")?.Value ?? DateTime.UtcNow.ToString("yyyyMMdd");
            history.SetVersion(version);

            var units = new List<(string Code, string Name, string? PrintSymbol)>();
            foreach (var element in root.Elements().Where(e => e.Name.LocalName is "unit" or "base-unit"))
            {
                var code = element.Attribute("Code")?.Value;
                if (string.IsNullOrWhiteSpace(code)) continue;
                var name = element.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value ?? code;
                var printSymbol = element.Elements().FirstOrDefault(e => e.Name.LocalName == "printSymbol")?.Value;
                units.Add((code, name, printSymbol));
            }
            if (units.Count == 0) throw new InvalidDataException("ucum-essence.xml contains no unit definitions.");

            // Dedupe before touching the DbContext — a repeated Code (shouldn't happen per the UCUM spec, but
            // defend against it anyway) would otherwise try to Add two brand-new UcumUnit instances sharing the
            // same key, which EF's change tracker rejects.
            var deduped = units.GroupBy(u => u.Code, StringComparer.Ordinal).Select(g => g.Last()).ToList();

            var existing = await _db.UcumUnits.ToDictionaryAsync(x => x.Code, StringComparer.Ordinal, cancellationToken);
            var incoming = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in deduped)
            {
                incoming.Add(unit.Code);
                if (!existing.TryGetValue(unit.Code, out var entity))
                {
                    entity = new UcumUnit(unit.Code, unit.Name, unit.PrintSymbol, true, version);
                    _db.UcumUnits.Add(entity);
                }
                else
                {
                    entity.Update(unit.Name, unit.PrintSymbol, version, true);
                }
            }
            foreach (var unit in existing.Values.Where(x => !incoming.Contains(x.Code)))
                unit.Update(unit.Name, unit.PrintSymbol, unit.Version, false);

            foreach (var activeVersion in await _db.UcumVersions.Where(x => x.IsActive).ToListAsync(cancellationToken)) activeVersion.SetActive(false);
            var existingVersion = await _db.UcumVersions.FirstOrDefaultAsync(x => x.Version == version, cancellationToken);
            if (existingVersion is not null) existingVersion.SetActive(true);
            else _db.UcumVersions.Add(new UcumVersion(version, null, null, true));

            history.Complete(deduped.Count);
            await _db.SaveChangesAsync(cancellationToken);
            return new(version, deduped.Count);
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            history.Fail(exception.ToString());
            _db.UcumImportHistory.Update(history);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
}
