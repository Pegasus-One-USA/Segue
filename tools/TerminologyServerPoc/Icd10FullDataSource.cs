using System.IO.Compression;

namespace FHIRBridge.Tools.TerminologyServerPoc;

/// <summary>
/// Downloads and parses the real, official CMS/CDC ICD-10-CM "order file" — no credentials
/// required, public download. This is the automatic-download step that replaces a person
/// manually fetching a zip from cms.gov/cdc.gov and uploading it through an admin screen.
///
/// File format is CMS's documented fixed-width "order file" layout (1-indexed):
///   1-5   order number
///   7-13  code, no decimal point (e.g. "A000")
///   15    valid-for-submission flag ("1" = billable leaf code, "0" = category header)
///   17-76 short description
///   77+   long description
/// Source: icd-10-cm-order-files.pdf, bundled in the same release zip.
/// </summary>
internal static class Icd10FullDataSource
{
    public const string ReleaseYear = "2025";
    public const string ZipUrl =
        "https://ftp.cdc.gov/pub/health_statistics/nchs/Publications/ICD10CM/2025/ICD10-CM%20Code%20Descriptions%202025.zip";
    private const string OrderFileEntryName = "icd10cm-order-2025.txt";

    public sealed record Concept(string Code, string Display, bool Billable);

    public static async Task<IReadOnlyList<Concept>> DownloadAndParseAsync(
        HttpClient http, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Downloading {ZipUrl} ...");
        await using var zipBytes = await http.GetStreamAsync(ZipUrl, ct);
        using var memory = new MemoryStream();
        await zipBytes.CopyToAsync(memory, ct);
        memory.Position = 0;
        progress?.Report($"Downloaded {memory.Length:N0} bytes.");

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var entry = archive.GetEntry(OrderFileEntryName)
            ?? throw new InvalidOperationException(
                $"Expected entry '{OrderFileEntryName}' was not found in the downloaded zip.");

        progress?.Report($"Parsing {OrderFileEntryName} ...");
        var results = new List<Concept>(100_000);

        using var reader = new StreamReader(entry.Open());
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.Length < 77)
            {
                continue;
            }

            var rawCode = line.Substring(6, 7).Trim();
            var billable = line.Substring(14, 1) == "1";
            var longDescription = line.Substring(76).Trim();

            if (string.IsNullOrEmpty(rawCode) || string.IsNullOrEmpty(longDescription))
            {
                continue;
            }

            var code = rawCode.Length > 3 ? string.Concat(rawCode.AsSpan(0, 3), ".", rawCode.AsSpan(3)) : rawCode;
            results.Add(new Concept(code, longDescription, billable));
        }

        progress?.Report($"Parsed {results.Count:N0} codes ({results.Count(c => c.Billable):N0} billable).");
        return results;
    }
}
