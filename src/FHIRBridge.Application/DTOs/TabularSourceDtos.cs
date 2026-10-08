namespace FHIRBridge.Application.DTOs;

/// <summary>An uploaded CSV, described without its content.</summary>
public sealed record TabularSourceFileDto(
    Guid Id,
    string FileName,
    int SizeBytes,
    int RowCount,
    IReadOnlyList<string> Columns,
    string? CreatedBy,
    DateTime CreatedOnUtc);

/// <summary>A database to save for CSV / SQL Table sources: its connection string is kept as a secret, never in the
/// workflow, and the database is listed by <paramref name="Name"/> for every workflow to pick.</summary>
public sealed record SaveTabularSqlConnectionRequest(string Engine, string ConnectionString, string? Name = null);

/// <summary>Rename a saved database, or replace its connection string (same secret, so every workflow using it follows).</summary>
public sealed record UpdateTabularSqlConnectionRequest(string? Name, string? ConnectionString);

/// <summary>A saved database. The node keeps the secret reference (what runs) and the id (what the form shows).</summary>
public sealed record TabularSqlConnectionDto(
    string Engine,
    string SecretKeyVaultName,
    string SecretName,
    Guid? Id = null,
    string? Name = null,
    string? CreatedBy = null,
    DateTime? UpdatedOnUtc = null);

/// <param name="Ok">The login connected and can only read.</param>
public sealed record TabularSqlConnectionTestDto(bool Ok, string Message);

/// <summary>Render the first rows of a file or query through templates, to check them before saving.</summary>
public sealed record TabularPreviewRequest(
    string Kind,
    Guid? FileId,
    string? SqlEngine,
    string? SecretKeyVaultName,
    string? SecretName,
    string? Query,
    string Templates);

/// <param name="Resources">The FHIR resources built from the first rows, as JSON. They hold the uploader's own data
/// and are returned only to a caller allowed to configure Tabular sources.</param>
/// <param name="MissingColumns">Columns the templates read that the table does not have.</param>
public sealed record TabularPreviewDto(
    IReadOnlyList<string> Columns,
    int RowsRead,
    IReadOnlyList<TabularPreviewResourceDto> Resources,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> MissingColumns);

public sealed record TabularPreviewResourceDto(string ResourceType, string? ResourceId, int RowNumber, string Json);

public sealed record TabularTemplatePresetDto(string ResourceType, string Template);

/// <summary>Check a Tabular source's per-type entries against its connection or files before saving.</summary>
/// <param name="Streams">The entries exactly as the node stores them (<c>tab_streams</c>).</param>
/// <param name="Preview">Also build the first rows of every entry that passes. Without it nothing reads a row.</param>
public sealed record TabularCheckRequest(
    string Kind,
    string? SqlEngine,
    string? SecretKeyVaultName,
    string? SecretName,
    string Streams,
    bool Preview = false);

/// <param name="AllPassed">Every entry passed: the source can be saved.</param>
public sealed record TabularCheckDto(bool AllPassed, IReadOnlyList<TabularStreamCheckDto> Streams);

/// <summary>One entry's result.</summary>
/// <param name="Problems">Why it cannot run: a missing table, view or column named by the database, a file that no
/// longer exists, a row filter column the file lacks, or template columns the query or file does not return.</param>
/// <param name="Columns">The columns the query or file returns.</param>
/// <param name="MissingColumns">Columns the template reads that the query or file does not return.</param>
/// <param name="RowsRead">Rows read for the preview; null when no preview was asked for or the entry failed.</param>
public sealed record TabularStreamCheckDto(
    int Index,
    string ResourceType,
    bool Passed,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> MissingColumns,
    int? RowsRead,
    IReadOnlyList<TabularPreviewResourceDto> Resources,
    IReadOnlyList<string> RowErrors);
