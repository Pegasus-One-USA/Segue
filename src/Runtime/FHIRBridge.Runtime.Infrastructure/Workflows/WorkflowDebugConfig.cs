using System.Text;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Workflows;

/// <summary>
/// Plain-text descriptions of the CONFIGURATION a workflow stage runs with (source settings, field mappings,
/// transformation rules, destination settings) for the WorkflowDebug trace. Everything here describes how the
/// workflow is set up, never data: no credentials or keys (only whether they are configured), no patient search
/// values or ids, no field default/constant values, no e-mail recipients, no connection strings, URLs reduced to
/// their host. Callers should still guard with <c>WorkflowDebug.StagesEnabled</c> so nothing is built when off.
/// </summary>
public static class WorkflowDebugConfig
{
    private const int MaxFields = 40;
    private const int MaxRules = 40;
    private const int MaxLength = 6000;

    public static string DescribeSource(FhirSourceConfiguration s)
    {
        var parts = new List<string>
        {
            $"type={s.SourceType}",
            s.ApplicationType is { } app ? $"application type={app}" : null,
            $"host={HostOf(s.BaseUrl)}",
            s.ResourceTypes is { Count: > 0 } types ? $"resource types=[{string.Join(", ", types.Take(30))}]" : null,
            $"page size={s.SearchCount}",
            $"max pages={s.MaxPages}",
            s.MaxRecords is { } max ? $"max records={max}" : null,
            !string.IsNullOrWhiteSpace(s.RetrievalMethod) ? $"retrieval={s.RetrievalMethod}" : null,
            !string.IsNullOrWhiteSpace(s.ExportScope) ? $"export scope={s.ExportScope}" : null,
            !string.IsNullOrWhiteSpace(s.OutputFormat) ? $"output format={s.OutputFormat}" : null,
            !string.IsNullOrWhiteSpace(s.RetryPolicy) ? $"retry policy={s.RetryPolicy}" : null,
            s.TimeoutSeconds is { } timeout ? $"timeout={timeout}s" : null,
            !string.IsNullOrWhiteSpace(s.AuthPlacement) ? $"credential placement={s.AuthPlacement}" : null,
            $"scopes configured={s.Scopes.Count}",
            s.LastUpdatedWatermarks is { Count: > 0 } w ? $"incremental sync on for {w.Count} resource type(s)" : "incremental sync off",
            s.Since is { } since ? $"since={since:yyyy-MM-dd}" : null,
            // Names only - the values of search parameters / criteria can carry patient identifiers.
            !string.IsNullOrWhiteSpace(s.SearchParameters) ? $"search parameter names=[{ParameterNames(s.SearchParameters)}]" : null,
            s.PatientIds is { Count: > 0 } ids ? $"cohort restricted to {ids.Count} patient(s)" : null,
            $"credentials: client id {(string.IsNullOrWhiteSpace(s.ClientId) ? "no" : "yes")}, "
                + $"private key {(string.IsNullOrWhiteSpace(s.PrivateKeyPem) ? "no" : "yes")}, "
                + $"client secret {(string.IsNullOrWhiteSpace(s.ClientSecret) ? "no" : "yes")}",
        };

        return Limit("Source settings: " + string.Join("; ", parts.Where(p => p is not null)));
    }

    public static string DescribeMapping(string resourceType, string? destinationObject, IReadOnlyCollection<MappingFieldDto> fields)
    {
        var sb = new StringBuilder();
        sb.Append($"Mapping configuration for ‘{resourceType}’ -> ‘{destinationObject}’: {fields.Count} field(s). ");
        foreach (var f in fields.Take(MaxFields))
        {
            sb.Append($"[{f.JsonPath} -> {f.TargetField} ({f.ValueType}");
            if (f.IsRequired) sb.Append(", required");
            if (f.IsUpsertKey) sb.Append(", upsert key");
            if (f.ArrayPolicy != default) sb.Append($", array: {f.ArrayPolicy}");
            if (!string.IsNullOrWhiteSpace(f.NormalizationType)) sb.Append($", normalize: {f.NormalizationType}");
            if (!string.IsNullOrWhiteSpace(f.TerminologyCodeJsonPath)) sb.Append(", terminology lookup");
            if (!string.IsNullOrWhiteSpace(f.CorrelationCodeJsonPath)) sb.Append($", correlate by code ({f.CorrelationCodeOperator ?? "Equals"})");
            if (!string.IsNullOrWhiteSpace(f.DefaultValue)) sb.Append(", default set");
            if (f.MaxLength is { } len) sb.Append($", max length {len}");
            sb.Append(")] ");
        }

        if (fields.Count > MaxFields)
        {
            sb.Append($"... and {fields.Count - MaxFields} more field(s).");
        }

        return Limit(sb.ToString().TrimEnd());
    }

    public static string DescribeRules(IReadOnlyList<TransformationRule> rules)
    {
        var sb = new StringBuilder($"Transformation rules on this node: {rules.Count}. ");
        foreach (var r in rules.OrderBy(r => r.Order).Take(MaxRules))
        {
            // Rule type and where it applies; ConfigJson (lookup tables, constants, patterns) is deliberately left out.
            sb.Append($"[#{r.Order} {r.NodeType} on {r.ResourceType ?? "any resource"}.{r.SourceField ?? "?"}");
            if (!string.IsNullOrWhiteSpace(r.DestinationField)) sb.Append($" -> {r.DestinationField}");
            sb.Append($"; scope {r.Scope}; phase {r.ExecutionPhase}; on null: {r.OnNull}; on error: {r.ErrorPolicy}");
            if (!r.IsEnabled) sb.Append("; DISABLED");
            sb.Append("] ");
        }

        if (rules.Count > MaxRules)
        {
            sb.Append($"... and {rules.Count - MaxRules} more rule(s).");
        }

        return Limit(sb.ToString().TrimEnd());
    }

    public static string DescribeDestination(DestinationConfiguration d, IEnumerable<(string Name, string? Value)> nodeSettings)
    {
        var parts = new List<string>
        {
            $"name=‘{d.Name}’",
            $"type={d.DestinationType}",
            $"target={SafeTarget(d.Target)}",
            $"credentials stored as secret reference: {(string.IsNullOrWhiteSpace(d.SecretReference?.SecretName) ? "no" : "yes")}",
        };
        parts.AddRange(nodeSettings.Where(s => !string.IsNullOrWhiteSpace(s.Value)).Select(s => $"{s.Name}={s.Value}"));
        return Limit("Destination settings: " + string.Join("; ", parts));
    }

    /// <summary>A URL becomes just its host; anything that could be an address list is withheld; names/paths pass.</summary>
    public static string SafeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "(none)";
        var cut = target.IndexOf(';');
        var value = (cut >= 0 ? target[..cut] : target).Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" or "sftp")
        {
            return uri.Host;
        }

        if (value.Contains('@') || value.Contains(','))
        {
            return "(withheld)";
        }

        return value.Length <= 120 ? value : value[..120] + "…";
    }

    private static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(not set)";

    private static string ParameterNames(string searchParameters) =>
        string.Join(", ", searchParameters.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)[0].Trim())
            .Where(n => n.Length > 0)
            .Distinct()
            .Take(20));

    private static string Limit(string text) => text.Length <= MaxLength ? text : text[..MaxLength] + "…";
}
