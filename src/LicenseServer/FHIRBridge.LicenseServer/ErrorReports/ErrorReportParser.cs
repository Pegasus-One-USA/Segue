using System.Globalization;
using System.Text;
using System.Text.Json;
using FHIRBridge.LicenseServer.Domain;

namespace FHIRBridge.LicenseServer.ErrorReports;

public sealed record ParsedErrorReport(
    string Format,
    DateTime? FromUtc,
    DateTime? ToUtc,
    DateTime? GeneratedAtUtc,
    string? ApplicationVersion,
    bool Truncated,
    IReadOnlyList<ErrorReportEntry> Entries,
    IReadOnlyList<ErrorReportCorrelation>? Correlations = null);

public sealed class ErrorReportFormatException : Exception
{
    public ErrorReportFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Reads the files produced by the product's Error Dashboard export (<c>GET /api/v1/operations/errors/export</c>):
/// the JSON report (an object with an <c>errors</c> array) or the flat CSV. Everything read is treated as
/// untrusted input: bounded in size and row count, lengths clamped, never executed or rendered unencoded.
/// </summary>
public static class ErrorReportParser
{
    public const int MaxRows = 20_000;
    private const int MaxShort = 400;
    private const int MaxMessage = 20_000;
    private const int MaxStack = 100_000;

    private static readonly string[] CsvHeader =
    [
        "ErrorReferenceId", "OccurredOnUtc", "Severity", "Category", "Module", "ExceptionType", "Message",
        "WhatToDo", "Cause", "Status", "CorrelationId", "ExecutionId", "WorkflowId", "EndpointId", "TraceId", "StackTrace",
    ];

    public static ParsedErrorReport Parse(string fileName, Stream content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".json" => ParseJson(content),
            ".csv" => ParseCsv(content),
            _ => throw new ErrorReportFormatException("Please choose a .json or .csv file exported from the Error Dashboard."),
        };
    }

    public static ParsedErrorReport ParseJson(Stream content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new ErrorReportFormatException("That file is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGet(root, "errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            {
                throw new ErrorReportFormatException("That JSON file is not an Error Dashboard report (no 'errors' list found).");
            }

            var entries = new List<ErrorReportEntry>();
            foreach (var item in errors.EnumerateArray())
            {
                if (entries.Count >= MaxRows)
                {
                    throw new ErrorReportFormatException($"The report has more than {MaxRows:N0} rows; export a shorter period.");
                }

                if (item.ValueKind != JsonValueKind.Object) continue;
                entries.Add(ToEntry(name => TryGet(item, name, out var v) ? Text(v) : null));
            }

            return new ParsedErrorReport(
                "json",
                Date(root, "fromUtc"),
                Date(root, "toUtc"),
                Date(root, "generatedAtUtc"),
                TryGet(root, "applicationVersion", out var version) ? Clamp(Text(version), MaxShort) : null,
                TryGet(root, "truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
                entries,
                ReadCorrelations(root));
        }
    }

    public static ParsedErrorReport ParseCsv(Stream content)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var rows = ReadCsv(reader.ReadToEnd());
        if (rows.Count == 0)
        {
            throw new ErrorReportFormatException("That CSV file is empty.");
        }

        var header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++) index.TryAdd(header[i], i);

        if (!index.ContainsKey("Message") || !index.ContainsKey("OccurredOnUtc"))
        {
            throw new ErrorReportFormatException(
                "That CSV does not look like an Error Dashboard export (expected columns: " + string.Join(", ", CsvHeader.Take(7)) + ", …).");
        }

        if (rows.Count - 1 > MaxRows)
        {
            throw new ErrorReportFormatException($"The report has more than {MaxRows:N0} rows; export a shorter period.");
        }

        var entries = new List<ErrorReportEntry>(rows.Count - 1);
        var correlations = new List<ErrorReportCorrelation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            string? Cell(string name) => index.TryGetValue(name, out var i) && i < row.Count ? UndoFormulaGuard(row[i]) : null;

            var entry = ToEntry(Cell);
            entries.Add(entry);

            // "CorrelationDetails" holds the run timeline as text on the first error row of each run.
            var details = Cell("CorrelationDetails");
            if (entry.CorrelationId is not null && details is not null && details.StartsWith("RUN |", StringComparison.Ordinal)
                && correlations.Count < MaxCorrelations && seen.Add(entry.CorrelationId))
            {
                correlations.Add(ParseCorrelationText(entry.CorrelationId, details));
            }
        }

        var dates = entries.Select(e => e.OccurredOnUtc).Where(d => d != default).ToList();
        return new ParsedErrorReport(
            "csv",
            dates.Count > 0 ? dates.Min() : null,
            dates.Count > 0 ? dates.Max() : null,
            null,
            null,
            false,
            entries,
            correlations);
    }

    /// <summary>Reads the CSV's text timeline ("RUN | …" header line, then "time | source | title | status | detail").</summary>
    private static ErrorReportCorrelation ParseCorrelationText(string correlationId, string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split('|', 5, StringSplitOptions.TrimEntries);

        DateTime? Dt(int i) =>
            i < header.Length && DateTime.TryParse(header[i], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

        int? Count(int slot)
        {
            var parts = header.Length > 4 ? header[4].Split('/') : [];
            return slot < parts.Length && int.TryParse(parts[slot], out var n) ? n : null;
        }

        var events = new List<Dictionary<string, string?>>();
        foreach (var line in lines.Skip(1))
        {
            if (events.Count >= MaxEventsPerCorrelation) break;
            if (line.StartsWith("...", StringComparison.Ordinal)) continue;
            var p = line.Split('|', 5, StringSplitOptions.TrimEntries);
            if (p.Length < 3) continue;
            events.Add(new Dictionary<string, string?>
            {
                ["occurredUtc"] = Clamp(p[0], 40),
                ["source"] = Clamp(p[1], 60),
                ["title"] = Clamp(p[2], MaxShort),
                ["status"] = p.Length > 3 ? Clamp(p[3], 60) : null,
                ["detail"] = p.Length > 4 ? Clamp(p[4], 1000) : null,
            });
        }

        return new ErrorReportCorrelation
        {
            CorrelationId = Clamp(correlationId, 150) ?? string.Empty,
            RunStatus = header.Length > 1 ? Clamp(header[1], 60) : null,
            RunStartedUtc = Dt(2),
            RunCompletedUtc = Dt(3),
            ExtractedCount = Count(0),
            MappedCount = Count(1),
            WrittenCount = Count(2),
            Truncated = text.Contains("more events omitted", StringComparison.Ordinal),
            EventsJson = JsonSerializer.Serialize(events),
        };
    }

    private const int MaxCorrelations = 100;
    private const int MaxEventsPerCorrelation = 200;

    /// <summary>Reads the optional "correlations" object ({ correlationId: { runStatus, …, events: [ … ] } }).</summary>
    private static List<ErrorReportCorrelation> ReadCorrelations(JsonElement root)
    {
        var result = new List<ErrorReportCorrelation>();
        if (!TryGet(root, "correlations", out var all) || all.ValueKind != JsonValueKind.Object) return result;

        foreach (var property in all.EnumerateObject())
        {
            if (result.Count >= MaxCorrelations) break;
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object) continue;

            var events = new List<Dictionary<string, string?>>();
            if (TryGet(value, "events", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in list.EnumerateArray())
                {
                    if (events.Count >= MaxEventsPerCorrelation) break;
                    if (ev.ValueKind != JsonValueKind.Object) continue;
                    string? Get(string n, int max) => TryGet(ev, n, out var v) ? Clamp(Text(v), max) : null;
                    events.Add(new Dictionary<string, string?>
                    {
                        ["occurredUtc"] = Get("occurredUtc", 40),
                        ["source"] = Get("source", 60),
                        ["title"] = Get("title", MaxShort),
                        ["status"] = Get("status", 60),
                        ["detail"] = Get("detail", 1000),
                    });
                }
            }

            int? Int(string n) => TryGet(value, n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

            result.Add(new ErrorReportCorrelation
            {
                CorrelationId = Clamp(property.Name, 150) ?? string.Empty,
                RunStatus = TryGet(value, "runStatus", out var rs) ? Clamp(Text(rs), 60) : null,
                RunStartedUtc = Date(value, "runStartedUtc"),
                RunCompletedUtc = Date(value, "runCompletedUtc"),
                ExtractedCount = Int("extractedCount"),
                MappedCount = Int("mappedCount"),
                WrittenCount = Int("writtenCount"),
                Truncated = TryGet(value, "truncated", out var t) && t.ValueKind == JsonValueKind.True,
                EventsJson = JsonSerializer.Serialize(events),
            });
        }

        return result;
    }

    private static ErrorReportEntry ToEntry(Func<string, string?> get)
    {
        _ = DateTime.TryParse(get("OccurredOnUtc"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var occurred);

        return new ErrorReportEntry
        {
            ErrorReferenceId = Clamp(get("ErrorReferenceId"), 60),
            OccurredOnUtc = occurred,
            Severity = Clamp(get("Severity"), 30) ?? "Error",
            Category = Clamp(get("Category"), 60),
            Module = Clamp(get("Module"), 150),
            ExceptionType = Clamp(get("ExceptionType"), 250) ?? "Unknown",
            Message = Clamp(get("Message"), MaxMessage) ?? string.Empty,
            WhatToDo = Clamp(get("WhatToDo"), 100),
            Cause = Clamp(get("Cause"), 1000),
            Status = Clamp(get("Status"), 30) ?? "Open",
            CorrelationId = Clamp(get("CorrelationId"), 150),
            ExecutionId = Clamp(get("ExecutionId"), 150),
            WorkflowId = Clamp(get("WorkflowId"), 150),
            WorkflowName = Clamp(get("WorkflowName"), 200),
            NodeName = Clamp(get("NodeName"), 200),
            NodeType = Clamp(get("NodeType"), 200),
            SourceName = Clamp(get("SourceName"), 200),
            DestinationName = Clamp(get("DestinationName"), 200),
            ResourceType = Clamp(get("ResourceType"), 200),
            EndpointId = Clamp(get("EndpointId"), MaxShort),
            TraceId = Clamp(get("TraceId"), 100),
            StackTrace = Clamp(get("StackTrace"), MaxStack),
        };
    }

    /// <summary>The export prefixes cells starting with = + - @ with an apostrophe so spreadsheets don't execute them.</summary>
    private static string UndoFormulaGuard(string value) =>
        value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => null,
    };

    private static DateTime? Date(JsonElement root, string name) =>
        TryGet(root, name, out var v)
        && DateTime.TryParse(Text(v), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;

    private static string? Clamp(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    /// <summary>Minimal RFC 4180 reader (quoted fields, doubled quotes, embedded commas and newlines).</summary>
    private static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                    break;
                default: field.Append(c); break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
