using System.Text;
using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services.Tabular;

/// <summary>
/// RFC 4180 CSV: comma, semicolon or tab separated (detected from the header line), double-quoted fields with
/// doubled quotes inside, quoted line breaks, CRLF or LF. The first line is the header. Blank lines are skipped.
/// Errors name a line number, never a value.
/// </summary>
public static class CsvTable
{
    public static TabularRows Parse(string text, int maxRows)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new BusinessRuleException("The file is empty.");
        }

        // A byte-order mark left in by Excel would otherwise become part of the first column's name.
        if (text[0] == '﻿')
        {
            text = text[1..];
        }

        var separator = DetectSeparator(text);
        var records = ReadRecords(text, separator).GetEnumerator();
        if (!records.MoveNext())
        {
            throw new BusinessRuleException("The file has no header line.");
        }

        var header = records.Current.Fields.Select(h => h.Trim()).ToList();
        if (header.Count == 0 || header.All(string.IsNullOrEmpty))
        {
            throw new BusinessRuleException("The header line has no column names.");
        }

        if (header.Count > TabularSourceSettings.MaxColumns)
        {
            throw new BusinessRuleException($"The file has {header.Count} columns; at most {TabularSourceSettings.MaxColumns} are supported.");
        }

        var duplicate = header.Where(h => h.Length > 0).GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new BusinessRuleException($"The column '{duplicate.Key}' appears more than once in the header.");
        }

        var rows = new List<IReadOnlyDictionary<string, string?>>();
        var truncated = false;
        while (records.MoveNext())
        {
            var (fields, line) = records.Current;
            if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0]))
            {
                continue;
            }

            if (fields.Count > header.Count)
            {
                throw new BusinessRuleException($"Line {line} has {fields.Count} fields but the header has {header.Count}.");
            }

            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++)
            {
                if (header[i].Length == 0)
                {
                    continue;
                }

                var value = i < fields.Count ? fields[i].Trim() : null;
                row[header[i]] = string.IsNullOrEmpty(value) ? null : value;
            }

            rows.Add(row);
        }

        return new TabularRows(header.Where(h => h.Length > 0).ToList(), rows, truncated);
    }

    private static char DetectSeparator(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var firstLine = end < 0 ? text : text[..end];
        var candidates = new[] { ',', ';', '\t' };
        return candidates.OrderByDescending(c => firstLine.Count(ch => ch == c)).First();
    }

    private static IEnumerable<(List<string> Fields, int Line)> ReadRecords(string text, char separator)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                if (c == '\n')
                {
                    line++;
                }

                field.Append(c);
                i++;
                continue;
            }

            if (c == '"' && field.ToString().Trim().Length == 0)
            {
                field.Clear();
                inQuotes = true;
                i++;
                continue;
            }

            if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
                i++;
                continue;
            }

            if (c is '\r' or '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                yield return (fields, recordLine);
                fields = [];
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                i++;
                line++;
                recordLine = line;
                continue;
            }

            field.Append(c);
            i++;
        }

        if (inQuotes)
        {
            throw new BusinessRuleException($"A quoted field starting on line {recordLine} is never closed.");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (fields, recordLine);
        }
    }
}
