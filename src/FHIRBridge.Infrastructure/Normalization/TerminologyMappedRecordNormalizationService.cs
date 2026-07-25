using System.Globalization;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Normalization;

public sealed class TerminologyMappedRecordNormalizationService : IMappedRecordNormalizationService
{
    public const string TerminologyDisplayNormalizationType = "TerminologyDisplay";
    public const string TerminologyTranslateNormalizationType = "TerminologyTranslate";

    private readonly ITerminologyLookupService _terminologyLookupService;
    private readonly ITerminologyTranslationService _terminologyTranslationService;
    private readonly ILogger<TerminologyMappedRecordNormalizationService> _logger;

    public TerminologyMappedRecordNormalizationService(
        ITerminologyLookupService terminologyLookupService,
        ITerminologyTranslationService terminologyTranslationService,
        ILogger<TerminologyMappedRecordNormalizationService> logger)
    {
        _terminologyLookupService = terminologyLookupService;
        _terminologyTranslationService = terminologyTranslationService;
        _logger = logger;
    }

    public async Task<MappedDestinationRecord> NormalizeAsync(
        MappedRecordNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        var terminologyFields = request.MappingFields
            .Where(field =>
                string.Equals(field.NormalizationType, TerminologyDisplayNormalizationType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.NormalizationType, TerminologyTranslateNormalizationType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (terminologyFields.Count == 0)
        {
            return request.Record;
        }

        using var document = JsonDocument.Parse(request.SourceJson);
        var values = new Dictionary<string, object?>(request.Record.Values, StringComparer.OrdinalIgnoreCase);

        foreach (var field in terminologyFields)
        {
            var system = ResolveString(document.RootElement, field.TerminologySystemJsonPath);
            var code = ResolveString(document.RootElement, field.TerminologyCodeJsonPath)
                ?? ResolveString(document.RootElement, field.JsonPath)
                ?? ResolveString(values, field.TargetField);

            if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            if (string.Equals(field.NormalizationType, TerminologyTranslateNormalizationType, StringComparison.OrdinalIgnoreCase))
            {
                // For translate, the destination code system is carried in the field's Format.
                var targetSystem = field.Format;
                if (string.IsNullOrWhiteSpace(targetSystem))
                {
                    continue;
                }

                var translation = await _terminologyTranslationService.TranslateAsync(system, code, targetSystem, cancellationToken);
                if (!string.IsNullOrWhiteSpace(translation?.TargetCode))
                {
                    values[field.TargetField] = translation.TargetCode;
                }

                continue;
            }

            var lookup = await _terminologyLookupService.LookupAsync(system, code, cancellationToken);
            if (string.IsNullOrWhiteSpace(lookup?.Display))
            {
                continue;
            }

            values[field.TargetField] = lookup.Display;
        }

        return request.Record with { Values = values };
    }

    private static string? ResolveString(JsonElement root, string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return null;
        }

        if (!TryResolve(root, jsonPath, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }

    private static string? ResolveString(IReadOnlyDictionary<string, object?> values, string targetField)
    {
        if (!values.TryGetValue(targetField, out var value) || value is null)
        {
            return null;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static bool TryResolve(JsonElement root, string jsonPath, out JsonElement value)
    {
        value = root;
        var normalizedPath = jsonPath.Trim();
        if (normalizedPath == "$")
        {
            return true;
        }

        if (!normalizedPath.StartsWith("$.", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in normalizedPath[2..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryResolveSegment(value, segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveSegment(JsonElement current, string segment, out JsonElement value)
    {
        value = current;
        var propertyName = segment;
        int? arrayIndex = null;
        var bracketIndex = segment.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex >= 0 && segment.EndsWith("]", StringComparison.Ordinal))
        {
            propertyName = segment[..bracketIndex];
            var indexText = segment[(bracketIndex + 1)..^1];
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIndex))
            {
                return false;
            }

            arrayIndex = parsedIndex;
        }

        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(propertyName, out current))
            {
                return false;
            }
        }

        if (arrayIndex is null)
        {
            value = current;
            return true;
        }

        if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= arrayIndex.Value)
        {
            return false;
        }

        value = current[arrayIndex.Value];
        return true;
    }
}
