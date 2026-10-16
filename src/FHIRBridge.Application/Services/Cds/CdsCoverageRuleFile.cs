using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Services.Cds;

/// <summary>One rule read from an import file, before it is checked against the database.</summary>
/// <param name="Payer">The payer name from the row (blank when the import names one payer for every row).</param>
/// <param name="Errors">Problems reading the row's cells (bad date, bad boolean, ...).</param>
public sealed record CdsCoverageRuleFileRow(int RowNumber, string? Payer, CdsCoverageRuleValues Values, IReadOnlyList<string> Errors);

/// <summary>A parsed import file.</summary>
public sealed record CdsCoverageRuleFileContent(IReadOnlyList<CdsCoverageRuleFileRow> Rows, IReadOnlyList<string> FileErrors);

/// <summary>
/// Reads and writes coverage rule files (CSV and JSON) — pure, no I/O. CSV: RFC 4180 (quoted cells, doubled quotes,
/// CRLF or LF), a header row with the columns in <see cref="CdsCoverageFileFormats.CsvColumns"/> (any order,
/// case-insensitive; payer, system, code and covered are required); several values in one cell separated by ';'.
/// JSON: <c>{"rules":[{...}]}</c> or a bare array, with the same names (docPurpose / infoNeeded as arrays or ';'
/// strings). Export guards against spreadsheet formula injection by prefixing risky cells with an apostrophe,
/// which import strips again.
/// </summary>
public static class CdsCoverageRuleFile
{
    /// <summary>Most rules one file may hold.</summary>
    public const int MaxRows = 5000;

    private static readonly string[] RequiredColumns = ["system", "code", "covered"];

    public static CdsCoverageRuleFileContent Parse(string format, string? content, bool payerColumnRequired)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new([], ["The file is empty."]);
        }

        return format switch
        {
            CdsCoverageFileFormats.Csv => ParseCsv(content, payerColumnRequired),
            CdsCoverageFileFormats.Json => ParseJson(content, payerColumnRequired),
            _ => new([], ["The format must be csv or json."]),
        };
    }

    // ---- CSV ----

    private static CdsCoverageRuleFileContent ParseCsv(string content, bool payerColumnRequired)
    {
        var records = ReadCsv(content.TrimStart('﻿'), out var csvError);
        if (csvError is not null)
        {
            return new([], [csvError]);
        }

        if (records.Count == 0)
        {
            return new([], ["The file has no header row."]);
        }

        var header = records[0].Select(cell => cell.Trim()).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileErrors = new List<string>();
        for (var i = 0; i < header.Length; i++)
        {
            var known = CdsCoverageFileFormats.CsvColumns.FirstOrDefault(c => string.Equals(c, header[i], StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                if (header[i].Length > 0)
                {
                    fileErrors.Add($"Unknown column '{Clip(header[i])}'. Columns: {string.Join(", ", CdsCoverageFileFormats.CsvColumns)}.");
                }

                continue;
            }

            if (!columns.TryAdd(known, i))
            {
                fileErrors.Add($"The column '{known}' appears twice.");
            }
        }

        foreach (var required in RequiredColumns.Concat(payerColumnRequired ? ["payer"] : []))
        {
            if (!columns.ContainsKey(required))
            {
                fileErrors.Add($"The required column '{required}' is missing.");
            }
        }

        if (fileErrors.Count > 0)
        {
            return new([], fileErrors);
        }

        var dataRows = records.Skip(1).Select((cells, index) => (Cells: cells, RowNumber: index + 2))
            .Where(row => row.Cells.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .ToArray();
        if (dataRows.Length > MaxRows)
        {
            return new([], [$"The file has {dataRows.Length} rules; at most {MaxRows} can be imported at once."]);
        }

        var rows = dataRows.Select(row =>
        {
            string? Cell(string name) =>
                columns.TryGetValue(name, out var index) && index < row.Cells.Count ? Unguard(row.Cells[index]) : null;
            return ToRow(row.RowNumber, Cell);
        }).ToArray();
        return new(rows, []);
    }

    /// <summary>RFC 4180 records; a quote inside an unquoted cell is kept as text.</summary>
    private static List<List<string>> ReadCsv(string content, out string? error)
    {
        error = null;
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var cellStarted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when !cellStarted:
                    inQuotes = true;
                    cellStarted = true;
                    break;
                case ',':
                    record.Add(cell.ToString());
                    cell.Clear();
                    cellStarted = false;
                    break;
                case '\r':
                    break;
                case '\n':
                    record.Add(cell.ToString());
                    cell.Clear();
                    cellStarted = false;
                    records.Add(record);
                    record = [];
                    break;
                default:
                    cell.Append(c);
                    cellStarted = true;
                    break;
            }
        }

        if (inQuotes)
        {
            error = "The file ends inside a quoted cell (a closing \" is missing).";
            return records;
        }

        if (cell.Length > 0 || record.Count > 0)
        {
            record.Add(cell.ToString());
            records.Add(record);
        }

        return records;
    }

    // ---- JSON ----

    private static CdsCoverageRuleFileContent ParseJson(string content, bool payerColumnRequired)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content.TrimStart('﻿'));
        }
        catch (JsonException)
        {
            return new([], ["The file is not valid JSON."]);
        }

        var array = root as JsonArray ?? (root as JsonObject)?["rules"] as JsonArray;
        if (array is null)
        {
            return new([], ["The JSON must be an array of rules or an object with a \"rules\" array."]);
        }

        if (array.Count > MaxRows)
        {
            return new([], [$"The file has {array.Count} rules; at most {MaxRows} can be imported at once."]);
        }

        var rows = new List<CdsCoverageRuleFileRow>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item)
            {
                rows.Add(new CdsCoverageRuleFileRow(i + 1, null, Empty, ["The rule is not a JSON object."]));
                continue;
            }

            var lookup = item.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            string? Cell(string name)
            {
                if (!lookup.TryGetValue(name, out var node) || node is null)
                {
                    return null;
                }

                if (node is JsonArray values)
                {
                    return string.Join(";", values.Select(v => v is JsonValue jv ? jv.ToString() : string.Empty));
                }

                return node is JsonValue value ? value.ToString() : null;
            }

            var row = ToRow(i + 1, Cell);
            if (payerColumnRequired && string.IsNullOrWhiteSpace(row.Payer))
            {
                row = row with { Errors = row.Errors.Append("The payer is required.").ToArray() };
            }

            rows.Add(row);
        }

        return new(rows, []);
    }

    // ---- Row mapping ----

    private static readonly CdsCoverageRuleValues Empty =
        new(null, null, false, null, null, null, null, null, null, null, null, null, null, true);

    private static CdsCoverageRuleFileRow ToRow(int rowNumber, Func<string, string?> cell)
    {
        var errors = new List<string>();
        var codeMatch = Blank(cell("codeMatch"))?.ToLowerInvariant() ?? CdsCoverageCodes.CodeMatch.Exact;
        if (!CdsCoverageCodes.CodeMatch.All.Contains(codeMatch))
        {
            errors.Add("codeMatch must be exact or prefix.");
        }

        var values = new CdsCoverageRuleValues(
            Blank(cell("system")),
            Blank(cell("code")),
            codeMatch == CdsCoverageCodes.CodeMatch.Prefix,
            Blank(cell("covered"))?.ToLowerInvariant(),
            Blank(cell("paNeeded"))?.ToLowerInvariant(),
            Blank(cell("docNeeded"))?.ToLowerInvariant(),
            Split(cell("docPurpose")),
            Split(cell("infoNeeded")),
            Blank(cell("reason")),
            Blank(cell("policyUrl")),
            Blank(cell("questionnaire")),
            Date(cell("effectiveFrom"), "effectiveFrom", errors),
            Date(cell("effectiveTo"), "effectiveTo", errors),
            Bool(cell("enabled"), errors));

        return new CdsCoverageRuleFileRow(rowNumber, Blank(cell("payer")), values, errors);
    }

    private static IReadOnlyList<string> Split(string? value) =>
        (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        // Codes are lower-case except v3-NullFlavor OTH ("other").
        .Select(v => string.Equals(v, CdsCoverageCodes.InfoNeeded.Other, StringComparison.OrdinalIgnoreCase) ? CdsCoverageCodes.InfoNeeded.Other : v.ToLowerInvariant())
        .ToArray();

    private static DateOnly? Date(string? value, string column, List<string> errors)
    {
        var trimmed = Blank(value);
        if (trimmed is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors.Add($"{column} must be a date as yyyy-MM-dd.");
        return null;
    }

    private static bool Bool(string? value, List<string> errors)
    {
        var trimmed = Blank(value)?.ToLowerInvariant();
        switch (trimmed)
        {
            case null or "true" or "yes" or "y" or "1":
                return true;
            case "false" or "no" or "n" or "0":
                return false;
            default:
                errors.Add("enabled must be true or false.");
                return true;
        }
    }

    // ---- Writing ----

    /// <summary>The rules as a file in <paramref name="format"/>; <paramref name="payerNames"/> maps payer id → name.</summary>
    public static string Write(string format, IEnumerable<CdsCoverageRule> rules, IReadOnlyDictionary<Guid, string> payerNames)
    {
        var ordered = rules
            .OrderBy(rule => payerNames.GetValueOrDefault(rule.PayerId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.System, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.EffectiveFrom)
            .Select(rule => Cells(rule, payerNames.GetValueOrDefault(rule.PayerId) ?? string.Empty))
            .ToArray();
        return format == CdsCoverageFileFormats.Json ? WriteJson(ordered) : WriteCsv(ordered);
    }

    /// <summary>A template with the header and two example rows (made-up payer and policy URLs).</summary>
    public static string Template(string format)
    {
        string[][] examples =
        [
            ["Example Health Plan", "http://www.ama-assn.org/go/cpt", "70553", "exact", "covered", "auth-needed", "clinical", "withpa",
                string.Empty, "MRI of the brain requires prior authorization.", "https://payer.example.org/policies/advanced-imaging",
                "http://payer.example.org/Questionnaire/advanced-imaging|1.0", "2026-01-01", string.Empty, "true"],
            ["Example Health Plan", "http://www.ama-assn.org/go/cpt", "9921", "prefix", "covered", "no-auth", string.Empty, string.Empty,
                string.Empty, "Office visits are covered without prior authorization.", string.Empty, string.Empty, string.Empty, string.Empty, "true"],
        ];
        return format == CdsCoverageFileFormats.Json ? WriteJson(examples) : WriteCsv(examples);
    }

    private static string[] Cells(CdsCoverageRule rule, string payerName) =>
    [
        payerName,
        rule.System,
        rule.Code,
        rule.CodePrefixMatch ? CdsCoverageCodes.CodeMatch.Prefix : CdsCoverageCodes.CodeMatch.Exact,
        rule.Covered,
        rule.PaNeeded ?? string.Empty,
        rule.DocNeeded ?? string.Empty,
        string.Join(";", rule.DocPurposes),
        string.Join(";", rule.InfoNeeded),
        rule.Reason ?? string.Empty,
        rule.PolicyUrl ?? string.Empty,
        rule.QuestionnaireCanonical ?? string.Empty,
        rule.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        rule.EffectiveTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        rule.IsEnabled ? "true" : "false",
    ];

    private static string WriteCsv(IEnumerable<string[]> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(",", CdsCoverageFileFormats.CsvColumns)).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(string.Join(",", row.Select(cell => Quote(Guard(cell))))).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string WriteJson(IEnumerable<string[]> rows)
    {
        var rules = new JsonArray();
        foreach (var row in rows)
        {
            var item = new JsonObject();
            for (var i = 0; i < CdsCoverageFileFormats.CsvColumns.Count; i++)
            {
                var column = CdsCoverageFileFormats.CsvColumns[i];
                var value = row[i];
                item[column] = column switch
                {
                    "docPurpose" or "infoNeeded" => new JsonArray(value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
                    "enabled" => JsonValue.Create(value == "true"),
                    _ => value.Length == 0 ? null : JsonValue.Create(value),
                };
            }

            rules.Add(item);
        }

        return new JsonObject { ["rules"] = rules }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{cell.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : cell;

    /// <summary>A cell a spreadsheet would run as a formula gets a leading apostrophe.</summary>
    private static string Guard(string cell) =>
        cell.Length > 0 && (cell[0] is '=' or '+' or '@' or '\t' or '\r' || (cell[0] == '-' && !double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            ? "'" + cell
            : cell;

    private static string Unguard(string cell) =>
        cell.Length > 1 && cell[0] == '\'' && cell[1] is '=' or '+' or '@' or '-' or '\t' or '\r' ? cell[1..] : cell;

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string Clip(string value) => value.Length > 40 ? value[..40] : value;
}
