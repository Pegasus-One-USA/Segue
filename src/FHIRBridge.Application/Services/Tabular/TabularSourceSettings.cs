using System.Text;
using System.Text.RegularExpressions;

namespace FHIRBridge.Application.Services.Tabular;

/// <summary>
/// Configuration keys, limits and identity of the Tabular source node (a CSV upload or a SQL query whose rows become
/// FHIR resources through templates). Shared by the Runtime executor, the API and the destination executor, so all
/// three read the same keys.
/// </summary>
public static partial class TabularSourceSettings
{
    public const string KindKey = "tab_kind";
    public const string DatasetKeyKey = "tab_datasetKey";
    public const string FileIdKey = "tab_fileId";
    public const string SqlEngineKey = "tab_sqlEngine";
    public const string SecretKeyVaultNameKey = "tab_secretKeyVaultName";
    public const string SecretNameKey = "tab_secretName";
    public const string QueryKey = "tab_query";
    public const string MaxRowsKey = "tab_maxRows";
    public const string TemplatesKey = "tab_templates";

    /// <summary>The per-type entries (<see cref="TabularStreams"/>): each resource type with its own query or file and
    /// template. A node without it is an older single-query node, run through <see cref="TemplatesKey"/>.</summary>
    public const string StreamsKey = "tab_streams";

    public const string CsvKind = "csv";
    public const string SqlKind = "sql";

    public const int DefaultMaxRows = 5000;
    public const int MaxAllowedRows = 50000;

    /// <summary>An uploaded CSV is held encrypted in the database, so it is capped.</summary>
    public const int MaxFileBytes = 10 * 1024 * 1024;

    public const int MaxColumns = 200;
    public const int MaxTemplates = 10;
    public const int MaxStreams = 30;
    public const int PreviewRows = 5;

    public static readonly IReadOnlyList<string> SqlEngines = ["sqlserver", "postgresql", "mysql"];

    /// <summary>
    /// The stable identity of the data set, standing in for a FHIR server's base URL wherever one is needed: the
    /// EHR write-back ledger keys every record on it. It comes from the user-chosen dataset key, never from a file
    /// or node id, so uploading a corrected copy of the same file, or cloning the workflow, keeps the same identity
    /// and the ledger still recognises rows already written.
    /// </summary>
    public static string? SourceBaseUrlFor(string? datasetKey)
    {
        var normalized = NormalizeDatasetKey(datasetKey);
        return normalized is null ? null : SourceUrlPrefix + normalized;
    }

    /// <summary>The run's source is a CSV / SQL Table data set (its identity is <see cref="SourceBaseUrlFor"/>'s), whose
    /// templates are written by the person who knows the target, so they may carry the target's own ids.</summary>
    public static bool IsTabularSource(string? sourceBaseUrl) =>
        sourceBaseUrl is not null && sourceBaseUrl.StartsWith(SourceUrlPrefix, StringComparison.Ordinal);

    private const string SourceUrlPrefix = "urn:fhirbridge:tabular:";

    /// <summary>Lower case letters, digits and hyphens, 3 to 64 characters; null when nothing usable is left.</summary>
    public static string? NormalizeDatasetKey(string? datasetKey)
    {
        if (string.IsNullOrWhiteSpace(datasetKey))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var c in datasetKey.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        var collapsed = Hyphens().Replace(builder.ToString(), "-").Trim('-');
        if (collapsed.Length > 64)
        {
            collapsed = collapsed[..64].Trim('-');
        }

        return collapsed.Length >= 3 ? collapsed : null;
    }

    public static int ClampMaxRows(string? configured) =>
        int.TryParse(configured, out var parsed) && parsed > 0 ? Math.Min(parsed, MaxAllowedRows) : DefaultMaxRows;

    [GeneratedRegex("-{2,}")]
    private static partial Regex Hyphens();
}
