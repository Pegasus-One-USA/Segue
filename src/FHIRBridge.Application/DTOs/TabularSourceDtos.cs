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

/// <summary>A SQL connection string to keep as a secret for a Tabular source. Never stored in the workflow.</summary>
public sealed record SaveTabularSqlConnectionRequest(string Engine, string ConnectionString);

/// <summary>Where the connection string was stored; the node keeps only this reference.</summary>
public sealed record TabularSqlConnectionDto(string Engine, string SecretKeyVaultName, string SecretName);

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
