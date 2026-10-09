using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Application.Services.Tabular;

/// <summary>
/// Everything the portal does with a Tabular source before it runs: upload a CSV (stored encrypted), keep a SQL
/// connection string as a secret, and preview what the templates build from the first rows. File contents, cell
/// values and connection strings are never logged.
/// </summary>
public interface ITabularSourceService
{
    Task<TabularSourceFileDto> UploadAsync(string fileName, string content, CancellationToken cancellationToken);

    Task<IReadOnlyList<TabularSourceFileDto>> ListAsync(CancellationToken cancellationToken);

    Task<TabularSourceFileDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<TabularSqlConnectionDto> SaveSqlConnectionAsync(SaveTabularSqlConnectionRequest request, CancellationToken cancellationToken);

    Task<TabularPreviewDto> PreviewAsync(TabularPreviewRequest request, CancellationToken cancellationToken);
}

public sealed class TabularSourceService : ITabularSourceService
{
    /// <summary>The secret bucket for Tabular SQL connection strings, resolved to the tenant's real vault.</summary>
    public const string SqlConnectionVaultName = "tabular-sources";

    private readonly ITabularSourceFileRepository _files;
    private readonly ITabularRowReader _reader;
    private readonly IPhiFieldEncryptor _encryptor;
    private readonly ISecretWriter _secretWriter;
    private readonly ITenantSecretVaultResolver _vaultResolver;
    private readonly ICurrentUserService _currentUser;
    private readonly ILogger<TabularSourceService> _logger;

    public TabularSourceService(
        ITabularSourceFileRepository files,
        ITabularRowReader reader,
        IPhiFieldEncryptor encryptor,
        ISecretWriter secretWriter,
        ITenantSecretVaultResolver vaultResolver,
        ICurrentUserService currentUser,
        ILogger<TabularSourceService> logger)
    {
        _files = files;
        _reader = reader;
        _encryptor = encryptor;
        _secretWriter = secretWriter;
        _vaultResolver = vaultResolver;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<TabularSourceFileDto> UploadAsync(string fileName, string content, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "upload.csv" : fileName.Trim());
        var size = Encoding.UTF8.GetByteCount(content);
        if (size > TabularSourceSettings.MaxFileBytes)
        {
            throw new BusinessRuleException($"The file is larger than {TabularSourceSettings.MaxFileBytes / (1024 * 1024)} MB.");
        }

        // Parsed now so a broken file is refused at upload, not at the first run.
        var table = CsvTable.Parse(content, TabularSourceSettings.MaxAllowedRows);
        if (table.Truncated)
        {
            throw new BusinessRuleException($"The file has more than {TabularSourceSettings.MaxAllowedRows} rows.");
        }

        var user = _currentUser.CurrentUser;
        var file = new TabularSourceFile(
            name,
            _encryptor.Encrypt(content),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
            size,
            table.Rows.Count,
            JsonSerializer.Serialize(table.Columns),
            user.Email ?? user.ExternalUserId,
            DateTime.UtcNow);
        await _files.AddAsync(file, cancellationToken);

        _logger.LogInformation(
            "Tabular source file {TabularFileId} uploaded: {RowCount} rows, {ColumnCount} columns, {SizeBytes} bytes.",
            file.Id, file.RowCount, table.Columns.Count, file.SizeBytes);
        return ToDto(file);
    }

    public async Task<IReadOnlyList<TabularSourceFileDto>> ListAsync(CancellationToken cancellationToken) =>
        (await _files.ListAsync(200, cancellationToken))
            .Select(f => new TabularSourceFileDto(
                f.Id, f.FileName, f.SizeBytes, f.RowCount, Columns(f.ColumnsJson), f.CreatedBy, f.CreatedOnUtc))
            .ToList();

    public async Task<TabularSourceFileDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToDto(await _files.GetAsync(id, cancellationToken) ?? throw new NotFoundException("Tabular source file", id));

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await _files.DeleteAsync(id, cancellationToken))
        {
            throw new NotFoundException("Tabular source file", id);
        }

        _logger.LogInformation("Tabular source file {TabularFileId} deleted.", id);
    }

    public async Task<TabularSqlConnectionDto> SaveSqlConnectionAsync(
        SaveTabularSqlConnectionRequest request, CancellationToken cancellationToken)
    {
        var engine = NormalizeEngine(request.Engine);
        if (string.IsNullOrWhiteSpace(request.ConnectionString) || request.ConnectionString.Length > 4000)
        {
            throw new BusinessRuleException("Enter the database connection string (at most 4000 characters).");
        }

        var reference = new SecretReference(
            _vaultResolver.ResolveVaultName(SqlConnectionVaultName),
            $"tabular-sql-{engine}-{Guid.NewGuid():N}");
        await _secretWriter.WriteSecretAsync(reference, request.ConnectionString.Trim(), cancellationToken);

        _logger.LogInformation(
            "Tabular SQL connection stored as secret {SecretName} in vault {KeyVaultName}.", reference.SecretName, reference.KeyVaultName);
        return new TabularSqlConnectionDto(engine, reference.KeyVaultName, reference.SecretName);
    }

    public async Task<TabularPreviewDto> PreviewAsync(TabularPreviewRequest request, CancellationToken cancellationToken)
    {
        var templates = TabularFhirTemplateEngine.ParseTemplates(request.Templates);
        var table = request.Kind switch
        {
            TabularSourceSettings.CsvKind when request.FileId is { } fileId && fileId != Guid.Empty =>
                await _reader.ReadFileAsync(fileId, TabularSourceSettings.PreviewRows, cancellationToken),
            TabularSourceSettings.SqlKind => await _reader.ReadSqlAsync(
                new TabularSqlQuery(
                    NormalizeEngine(request.SqlEngine),
                    new SecretReference(request.SecretKeyVaultName ?? string.Empty, request.SecretName ?? string.Empty),
                    request.Query ?? string.Empty),
                TabularSourceSettings.PreviewRows,
                cancellationToken),
            TabularSourceSettings.CsvKind => throw new BusinessRuleException("Upload a CSV file first."),
            _ => throw new BusinessRuleException("Choose CSV file or SQL query."),
        };

        var built = TabularResourceBuilder.Build(table, templates);
        var columns = new HashSet<string>(table.Columns, StringComparer.OrdinalIgnoreCase);
        var missing = TabularFhirTemplateEngine.ReferencedColumns(templates)
            .Where(c => !columns.Contains(c))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TabularPreviewDto(
            table.Columns,
            table.Rows.Count,
            built.Resources.Select(r => new TabularPreviewResourceDto(r.ResourceType, r.ResourceId, r.RowNumber, r.Json)).ToList(),
            built.Errors,
            missing);
    }

    public static string NormalizeEngine(string? engine)
    {
        var normalized = (engine ?? string.Empty).Trim().ToLowerInvariant();
        return TabularSourceSettings.SqlEngines.Contains(normalized)
            ? normalized
            : throw new BusinessRuleException("Choose SQL Server, PostgreSQL or MySQL.");
    }

    private static TabularSourceFileDto ToDto(TabularSourceFile file) => new(
        file.Id, file.FileName, file.SizeBytes, file.RowCount, Columns(file.ColumnsJson), file.CreatedBy, file.CreatedOnUtc);

    private static IReadOnlyList<string> Columns(string columnsJson) =>
        JsonSerializer.Deserialize<List<string>>(columnsJson) ?? [];
}
