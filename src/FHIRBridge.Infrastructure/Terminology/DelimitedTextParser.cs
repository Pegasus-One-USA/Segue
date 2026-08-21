namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Shared, format-tolerant reader for the header-having tab/pipe/comma-delimited exports CMS/CDC distribute for
/// ICD-10-PCS, HCPCS Level II, and CVX (unlike RxNorm's fixed-column RRF or LOINC's documented CSV, these
/// vocabularies don't have a single universally-stable byte layout across every distribution channel — this
/// detects whichever delimiter the header row uses and looks columns up by name, same defensive-fallback
/// pattern as <c>LoincSynchronizationService.Get</c>/<c>RxNormImportService</c>'s row lookups, tolerant to the
/// column-name variations different releases/downstream mirrors use for the same field).
/// </summary>
public static class DelimitedTextParser
{
    public static (Dictionary<string, int> Index, IEnumerable<string[]> Rows) Read(Stream stream)
    {
        // Deliberately no `using` here: ReadRows is a `yield return` iterator, so it doesn't actually run until
        // the caller's foreach starts pulling from it — disposing the reader before that (e.g. via a `using`
        // scoped to this method) closes it out from under the not-yet-started enumeration. Ownership of the
        // reader is handed to ReadRows's own `using` instead, which — inside an iterator method — correctly
        // disposes only once enumeration finishes or is abandoned.
        var reader = new StreamReader(stream);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("The file has no header row.");
        var delimiter = DetectDelimiter(headerLine);
        var headers = headerLine.Split(delimiter);
        var index = headers
            .Select((name, i) => (name: name.Trim().Trim('"'), i))
            .Where(x => !string.IsNullOrWhiteSpace(x.name))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

        return (index, ReadRows(reader, delimiter));
    }

    private static IEnumerable<string[]> ReadRows(StreamReader reader, char delimiter)
    {
        using (reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                yield return line.Split(delimiter).Select(field => field.Trim().Trim('"')).ToArray();
            }
        }
    }

    private static char DetectDelimiter(string headerLine) => headerLine switch
    {
        _ when headerLine.Contains('\t') => '\t',
        _ when headerLine.Contains('|') => '|',
        _ => ',',
    };

    public static string Get(Dictionary<string, int> index, string[] row, params string[] candidateNames)
    {
        foreach (var name in candidateNames)
            if (index.TryGetValue(name, out var i) && i < row.Length)
                return row[i];
        return string.Empty;
    }
}
