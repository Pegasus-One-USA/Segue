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

    Task<IReadOnlyList<TabularSqlConnectionDto>> ListSqlConnectionsAsync(CancellationToken cancellationToken);

    Task<TabularSqlConnectionDto> UpdateSqlConnectionAsync(Guid id, UpdateTabularSqlConnectionRequest request, CancellationToken cancellationToken);

    Task DeleteSqlConnectionAsync(Guid id, CancellationToken cancellationToken);

    Task<TabularSqlConnectionTestDto> TestSqlConnectionAsync(Guid id, CancellationToken cancellationToken);

    Task<TabularPreviewDto> PreviewAsync(TabularPreviewRequest request, CancellationToken cancellationToken);

    Task<TabularCheckDto> CheckAsync(TabularCheckRequest request, CancellationToken cancellationToken);
}

public sealed class TabularSourceService : ITabularSourceService
{
    /// <summary>The secret bucket for Tabular SQL connection strings, resolved to the tenant's real vault.</summary>
    public const string SqlConnectionVaultName = "tabular-sources";

    private readonly ITabularSourceFileRepository _files;
    private readonly ITabularSqlConnectionRepository _connections;
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
        ILogger<TabularSourceService> logger,
        ITabularSqlConnectionRepository? connections = null)
    {
        _files = files;
        _connections = connections ?? new NoSavedConnections();
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
        RequireConnectionString(request.ConnectionString);
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? $"{EngineLabel(engine)} {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}"
            : request.Name.Trim();
        await RequireUniqueNameAsync(name, null, cancellationToken);

        var reference = new SecretReference(
            _vaultResolver.ResolveVaultName(SqlConnectionVaultName),
            $"tabular-sql-{engine}-{Guid.NewGuid():N}");
        await _secretWriter.WriteSecretAsync(reference, request.ConnectionString.Trim(), cancellationToken);

        var user = _currentUser.CurrentUser;
        var saved = new TabularSqlConnection(name, engine, reference.KeyVaultName, reference.SecretName, user.Email ?? user.ExternalUserId, DateTime.UtcNow);
        await _connections.AddAsync(saved, cancellationToken);

        _logger.LogInformation(
            "Tabular SQL connection {TabularSqlConnectionId} stored as secret {SecretName} in vault {KeyVaultName}.",
            saved.Id, reference.SecretName, reference.KeyVaultName);
        return ToDto(saved);
    }

    public async Task<IReadOnlyList<TabularSqlConnectionDto>> ListSqlConnectionsAsync(CancellationToken cancellationToken) =>
        (await _connections.ListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<TabularSqlConnectionDto> UpdateSqlConnectionAsync(
        Guid id, UpdateTabularSqlConnectionRequest request, CancellationToken cancellationToken)
    {
        var saved = await _connections.GetAsync(id, cancellationToken) ?? throw new NotFoundException("Database connection", id);
        if (!string.IsNullOrWhiteSpace(request.Name) && !string.Equals(request.Name.Trim(), saved.Name, StringComparison.Ordinal))
        {
            await RequireUniqueNameAsync(request.Name, id, cancellationToken);
            saved.Rename(request.Name);
        }

        if (!string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            RequireConnectionString(request.ConnectionString);
            // Same secret: every node that reads this database keeps its reference and gets the new connection string.
            await _secretWriter.WriteSecretAsync(
                new SecretReference(saved.KeyVaultName, saved.SecretName), request.ConnectionString.Trim(), cancellationToken);
            _logger.LogInformation("Tabular SQL connection {TabularSqlConnectionId}: connection string replaced.", saved.Id);
        }

        saved.Touch(DateTime.UtcNow);
        await _connections.UpdateAsync(saved, cancellationToken);
        return ToDto(saved);
    }

    public async Task DeleteSqlConnectionAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await _connections.DeleteAsync(id, cancellationToken))
        {
            throw new NotFoundException("Database connection", id);
        }

        _logger.LogInformation("Tabular SQL connection {TabularSqlConnectionId} deleted.", id);
    }

    /// <summary>Connects with the saved login, checks it can only read, and runs nothing but <c>SELECT 1</c>.</summary>
    public async Task<TabularSqlConnectionTestDto> TestSqlConnectionAsync(Guid id, CancellationToken cancellationToken)
    {
        var saved = await _connections.GetAsync(id, cancellationToken) ?? throw new NotFoundException("Database connection", id);
        try
        {
            await _reader.DescribeSqlAsync(
                new TabularSqlQuery(saved.Engine, new SecretReference(saved.KeyVaultName, saved.SecretName), "SELECT 1 AS ok"),
                cancellationToken);
            return new TabularSqlConnectionTestDto(true, "Connected. The login can only read.");
        }
        catch (BusinessRuleException ex)
        {
            return new TabularSqlConnectionTestDto(false, ex.Message);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            // The driver's message can echo the server name but never a password; the exception type is enough here.
            _logger.LogWarning("Tabular SQL connection {TabularSqlConnectionId} test failed: {ErrorType}.", id, ex.GetType().Name);
            return new TabularSqlConnectionTestDto(false, "Could not connect. Check the server, database, login and password.");
        }
    }

    private async Task RequireUniqueNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (name.Trim().Length is 0 or > 200)
        {
            throw new BusinessRuleException("Name the database connection (at most 200 characters).");
        }

        if (await _connections.FindByNameAsync(name, cancellationToken) is { } existing && existing.Id != exceptId)
        {
            throw new BusinessRuleException($"A database connection named '{name.Trim()}' already exists.");
        }
    }

    private static void RequireConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Length > 4000)
        {
            throw new BusinessRuleException("Enter the database connection string (at most 4000 characters).");
        }
    }

    private static string EngineLabel(string engine) => engine switch
    {
        "sqlserver" => "SQL Server",
        "postgresql" => "PostgreSQL",
        _ => "MySQL",
    };

    private static TabularSqlConnectionDto ToDto(TabularSqlConnection c) =>
        new(c.Engine, c.KeyVaultName, c.SecretName, c.Id, c.Name, c.CreatedBy, c.UpdatedOnUtc);

    /// <summary>Hosts that have not registered the repository (older test wiring) save the secret only.</summary>
    private sealed class NoSavedConnections : ITabularSqlConnectionRepository
    {
        public Task AddAsync(TabularSqlConnection connection, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TabularSqlConnection?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<TabularSqlConnection?>(null);

        public Task<TabularSqlConnection?> FindByNameAsync(string name, CancellationToken cancellationToken) => Task.FromResult<TabularSqlConnection?>(null);

        public Task<IReadOnlyList<TabularSqlConnection>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TabularSqlConnection>>([]);

        public Task UpdateAsync(TabularSqlConnection connection, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
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

    /// <summary>
    /// Checks every per-type entry against what it reads, without reading a row unless a preview is asked for: a SQL
    /// entry's query is described by the database (its tables, views and columns must exist, and it must return the
    /// columns its template reads); a CSV entry's file must still exist and have the template's and the row filter's
    /// columns, taken from the header recorded at upload. A problem in one entry does not stop the others' checks.
    /// </summary>
    public async Task<TabularCheckDto> CheckAsync(TabularCheckRequest request, CancellationToken cancellationToken)
    {
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is not (TabularSourceSettings.CsvKind or TabularSourceSettings.SqlKind))
        {
            throw new BusinessRuleException("Choose CSV file or SQL query.");
        }

        var streams = TabularStreams.Parse(request.Streams, kind);
        var engine = kind == TabularSourceSettings.SqlKind ? NormalizeEngine(request.SqlEngine) : null;
        var secret = new SecretReference(request.SecretKeyVaultName ?? string.Empty, request.SecretName ?? string.Empty);

        var results = new List<TabularStreamCheckDto>();
        for (var i = 0; i < streams.Count; i++)
        {
            results.Add(await CheckStreamAsync(i, streams[i], engine, secret, request.Preview, cancellationToken));
        }

        return new TabularCheckDto(results.All(r => r.Passed), results);
    }

    private async Task<TabularStreamCheckDto> CheckStreamAsync(
        int index, TabularStream stream, string? engine, SecretReference secret, bool preview, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        IReadOnlyList<string> columns = [];
        try
        {
            if (engine is not null)
            {
                columns = await _reader.DescribeSqlAsync(new TabularSqlQuery(engine, secret, stream.Query!), cancellationToken);
            }
            else
            {
                var file = await _files.GetAsync(stream.FileId!.Value, cancellationToken);
                if (file is null)
                {
                    problems.Add("The CSV file no longer exists. Upload it again.");
                }
                else
                {
                    columns = Columns(file.ColumnsJson);
                    if (stream.RowFilterColumn is { } filter && !columns.Contains(filter, StringComparer.OrdinalIgnoreCase))
                    {
                        problems.Add($"The row filter column '{filter}' is not in {file.FileName}.");
                    }
                }
            }
        }
        catch (BusinessRuleException ex)
        {
            problems.Add(ex.Message);
        }

        var known = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        var missing = problems.Count > 0
            ? []
            : TabularFhirTemplateEngine.ReferencedColumns([stream.Template])
                .Where(c => !known.Contains(c))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (missing.Count > 0)
        {
            problems.Add($"The template reads {(missing.Count == 1 ? "a column" : "columns")} the "
                         + $"{(engine is null ? "file does" : "query does")} not return: {string.Join(", ", missing)}.");
        }

        int? rowsRead = null;
        IReadOnlyList<TabularPreviewResourceDto> resources = [];
        IReadOnlyList<string> rowErrors = [];
        if (preview && problems.Count == 0)
        {
            try
            {
                var rows = engine is not null
                    ? await _reader.ReadSqlAsync(new TabularSqlQuery(engine, secret, stream.Query!), TabularSourceSettings.PreviewRows, cancellationToken)
                    : await ReadFilePreviewAsync(stream, cancellationToken);
                var built = TabularResourceBuilder.Build(rows, [stream.Template]);
                rowsRead = rows.Rows.Count;
                resources = built.Resources
                    .Select(r => new TabularPreviewResourceDto(r.ResourceType, r.ResourceId, r.RowNumber, r.Json))
                    .ToList();
                rowErrors = built.Errors;
            }
            catch (BusinessRuleException ex)
            {
                problems.Add(ex.Message);
            }
        }

        return new TabularStreamCheckDto(
            index, stream.ResourceType, problems.Count == 0, problems, columns, missing, rowsRead, resources, rowErrors);
    }

    /// <summary>The first rows the entry keeps: the whole file is read so a row filter can find them, then cut.</summary>
    private async Task<TabularRows> ReadFilePreviewAsync(TabularStream stream, CancellationToken cancellationToken)
    {
        var rows = TabularStreams.ApplyRowFilter(
            await _reader.ReadFileAsync(stream.FileId!.Value, TabularSourceSettings.MaxAllowedRows, cancellationToken), stream);
        return rows with { Rows = rows.Rows.Take(TabularSourceSettings.PreviewRows).ToList() };
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
