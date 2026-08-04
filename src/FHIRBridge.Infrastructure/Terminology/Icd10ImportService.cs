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
/// Imports an uploaded CMS ICD-10-CM "Code Descriptions in Tabular Order" release archive into the local
/// terminology schema. The order file is fixed-width text (order number, code, billable flag, short/long
/// description) and represents the complete code set for the year, so — unlike SNOMED CT's concepts —
/// no upsert-against-existing logic is needed: each import truncates and bulk-copies the whole table.
/// </summary>
public sealed class Icd10ImportService : IIcd10ImportService
{
    private readonly FHIRBridgeDbContext _db;
    public Icd10ImportService(FHIRBridgeDbContext db) => _db = db;

    public async Task<Icd10ImportResult> ImportAsync(Stream releaseZipStream, CancellationToken cancellationToken)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "App_Data", "Terminology", "Icd10");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"upload-{Guid.NewGuid():N}.zip");

        await using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await releaseZipStream.CopyToAsync(fileStream, cancellationToken);

        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var orderEntry = FindOrderEntry(archive) ?? throw new InvalidDataException("No ICD-10-CM order file was found in the release archive.");
            var version = ExtractVersion(orderEntry.Name);

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var connection = (SqlConnection)_db.Database.GetDbConnection();
            var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();

            var codeCount = 0;
            IEnumerable<object?[]> CodeRows()
            {
                foreach (var row in ReadOrderRows(orderEntry))
                {
                    codeCount++;
                    yield return new object?[] { row.Code, row.OrderNumber, row.IsBillable, row.ShortDescription, row.LongDescription, version, true, DateTime.UtcNow, DateTime.UtcNow };
                }
            }

            await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE terminology.Icd10Codes", cancellationToken);
            await TerminologyBulkCopy.WriteAsync(connection, sqlTransaction, "terminology.Icd10Codes",
                ["Code", "OrderNumber", "IsBillable", "ShortDescription", "LongDescription", "Version", "IsActive", "CreatedOnUtc", "ModifiedOnUtc"],
                CodeRows(), cancellationToken);

            if (codeCount == 0) throw new InvalidDataException("The release contains no ICD-10-CM codes.");

            foreach (var activeVersion in await _db.Icd10Versions.Where(x => x.IsActive).ToListAsync(cancellationToken)) activeVersion.SetActive(false);
            _db.Icd10Versions.Add(new Icd10Version(version, null, null, true));

            var history = new Icd10ImportHistory(version, null);
            history.Complete(codeCount);
            _db.Icd10ImportHistory.Add(history);
            await _db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new(version, codeCount);
        }
        catch (Exception exception)
        {
            _db.ChangeTracker.Clear();
            var history = new Icd10ImportHistory(null, null);
            history.Fail(exception.ToString());
            _db.Icd10ImportHistory.Add(history);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static ZipArchiveEntry? FindOrderEntry(ZipArchive archive) =>
        archive.Entries.FirstOrDefault(x =>
            x.FullName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
            x.FullName.Contains("order", StringComparison.OrdinalIgnoreCase) &&
            !x.FullName.Contains("addenda", StringComparison.OrdinalIgnoreCase));

    private static string ExtractVersion(string fileName)
    {
        var match = Regex.Match(fileName, @"(\d{4})");
        if (!match.Success) throw new InvalidDataException("Could not determine the release year from the order file name " + fileName + ".");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Parses the CMS ICD-10-CM order file's fixed-width layout: order number (cols 1-5), code (cols 7-13),
    /// billable flag (col 15, '1' = valid/billable code, '0' = header/category), short description
    /// (cols 17-76), long description (col 77 to end of line).
    /// </summary>
    private static IEnumerable<OrderRow> ReadOrderRows(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 77) continue;
            var code = line.Substring(6, 7).Trim();
            if (code.Length == 0) continue;
            var orderNumber = int.TryParse(line.AsSpan(0, 5), out var order) ? order : 0;
            var isBillable = line[14] == '1';
            var shortDescription = line.Substring(16, 60).Trim();
            var longDescription = line.Substring(76).Trim();
            yield return new(code, orderNumber, isBillable, shortDescription, longDescription);
        }
    }

    private sealed record OrderRow(string Code, int OrderNumber, bool IsBillable, string ShortDescription, string LongDescription);
}
