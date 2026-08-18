using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Imports an openFDA National Drug Code Directory bulk-export archive (a zip containing one JSON file shaped
/// like openFDA's standard <c>{"results": [...]}</c> envelope) into the local terminology schema. A full export
/// represents the complete current directory, so — like ICD-10-CM/RxNorm — the whole terminology.NdcProducts
/// table is truncated and reloaded rather than upserted.
/// </summary>
public sealed class NdcImportService : INdcImportService
{
    private readonly FHIRBridgeDbContext _db;
    public NdcImportService(FHIRBridgeDbContext db) => _db = db;

    public async Task<NdcImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken)
    {
        var history = new NdcImportHistory(null, null);
        _db.NdcImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
            var entry = archive.Entries.FirstOrDefault(x => x.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("No JSON file was found in the NDC release archive.");

            var version = DateTime.UtcNow.ToString("yyyyMMdd");
            history.SetVersion(version);

            using var stream = entry.Open();
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The NDC release JSON did not contain a 'results' array.");

            var products = new List<(string ProductNdc, string GenericName, string? BrandName, string? DosageForm)>();
            foreach (var record in results.EnumerateArray())
            {
                var productNdc = TryGetString(record, "product_ndc");
                if (string.IsNullOrWhiteSpace(productNdc)) continue;
                products.Add((productNdc, TryGetString(record, "generic_name") ?? productNdc, TryGetString(record, "brand_name"), TryGetString(record, "dosage_form")));
            }
            if (products.Count == 0) throw new InvalidDataException("The NDC release contains no products.");

            // openFDA's export has multiple records per product_ndc (one per package size/listing) — dedupe
            // before touching the DbContext, keeping the last occurrence, or two brand-new NdcProduct instances
            // with the same key both get Added in the same SaveChanges and EF's change tracker rejects the
            // second one as a duplicate key conflict.
            var deduped = products
                .GroupBy(p => p.ProductNdc, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();

            var existing = await _db.NdcProducts.ToDictionaryAsync(x => x.ProductNdc, cancellationToken);
            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in deduped)
            {
                incoming.Add(product.ProductNdc);
                if (!existing.TryGetValue(product.ProductNdc, out var entity))
                {
                    entity = new NdcProduct(product.ProductNdc, product.GenericName, product.BrandName, product.DosageForm, true, version);
                    _db.NdcProducts.Add(entity);
                }
                else
                {
                    entity.Update(product.GenericName, product.BrandName, product.DosageForm, version, true);
                }
            }
            foreach (var product in existing.Values.Where(x => !incoming.Contains(x.ProductNdc)))
                product.Update(product.GenericName, product.BrandName, product.DosageForm, product.Version, false);

            foreach (var activeVersion in await _db.NdcVersions.Where(x => x.IsActive).ToListAsync(cancellationToken)) activeVersion.SetActive(false);
            _db.NdcVersions.Add(new NdcVersion(version, null, null, true));

            history.Complete(deduped.Count);
            await _db.SaveChangesAsync(cancellationToken);
            return new(version, deduped.Count);
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            history.Fail(exception.ToString());
            _db.NdcImportHistory.Update(history);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (File.Exists(zipFilePath)) File.Delete(zipFilePath);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
