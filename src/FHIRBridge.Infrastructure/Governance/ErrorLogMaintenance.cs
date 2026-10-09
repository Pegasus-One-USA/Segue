using System.Data;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>Persists <see cref="ErrorLogSettings"/> as one JSON system setting.</summary>
public sealed class SystemSettingsErrorLogSettingsStore : IErrorLogSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ISystemSettingRepository _settings;

    public SystemSettingsErrorLogSettingsStore(ISystemSettingRepository settings)
    {
        _settings = settings;
    }

    public async Task<ErrorLogSettings> GetAsync(CancellationToken cancellationToken)
    {
        var row = await _settings.GetByKeyAsync(ErrorLogSettings.SettingKey, cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Value))
        {
            return ErrorLogSettings.Default;
        }

        try
        {
            return (JsonSerializer.Deserialize<ErrorLogSettings>(row.Value, Json) ?? ErrorLogSettings.Default).Normalize();
        }
        catch (JsonException)
        {
            // A hand-edited / corrupt value must never stop errors being captured: fall back to the defaults.
            return ErrorLogSettings.Default;
        }
    }

    public Task SaveAsync(ErrorLogSettings settings, CancellationToken cancellationToken) =>
        _settings.UpsertAsync(
            ErrorLogSettings.SettingKey,
            JsonSerializer.Serialize(settings.Normalize(), Json),
            "Error log handling: which entries are captured and how long they are kept.",
            cancellationToken);
}

/// <summary>
/// Singleton, always-synchronous view of the saved settings. Reads the store through a fresh scope at most every
/// 30 seconds in the background (never on the calling thread), so error capture and logging never wait on the
/// database for a setting.
/// </summary>
public sealed class SettingsErrorCapturePolicy : IErrorCapturePolicy
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private volatile ErrorLogSettings _current = ErrorLogSettings.Default;
    private long _loadedAtTicks;
    private int _refreshing;

    public SettingsErrorCapturePolicy(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public ErrorLogSettings Current
    {
        get
        {
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) > MaxAge.Ticks
                && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RefreshCoreAsync(CancellationToken.None);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshing, 0);
                    }
                });
            }

            return _current;
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(cancellationToken);

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        // Mark first: a failing database must not make every call retry.
        Interlocked.Exchange(ref _loadedAtTicks, DateTime.UtcNow.Ticks);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IErrorLogSettingsStore>();
            _current = await store.GetAsync(cancellationToken);
        }
        catch
        {
            // keep the last known settings
        }
    }
}

public sealed class NullErrorLogMaintenanceService : IErrorLogMaintenanceService
{
    public Task<ErrorLogStorageDto> GetStorageAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ErrorLogStorageDto(0, null, null, null));

    public Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<ErrorLogDeleteResult> DeleteAndReclaimAsync(DateTime? olderThanUtc, CancellationToken cancellationToken) =>
        Task.FromResult(new ErrorLogDeleteResult(0, false, "No database is configured."));
}

public sealed class EfErrorLogMaintenanceService : IErrorLogMaintenanceService
{
    // Kept well under SQL Server's 2,100-parameter ceiling in case a provider expands Contains() into parameters.
    private const int BatchSize = 1000;

    private readonly FHIRBridgeDbContext _db;
    private readonly int _floorDays;

    public EfErrorLogMaintenanceService(FHIRBridgeDbContext db, IOptions<ErrorCaptureOptions> options)
    {
        _db = db;
        _floorDays = Math.Max(0, options.Value.MinimumRetentionDays);
    }

    /// <summary>The newest cutoff any delete may use: nothing recorded within the retention floor is ever removed.</summary>
    private DateTime ClampToFloor(DateTime cutoffUtc)
    {
        var floor = DateTime.UtcNow.AddDays(-_floorDays);
        return cutoffUtc < floor ? cutoffUtc : floor;
    }

    public async Task<ErrorLogStorageDto> GetStorageAsync(CancellationToken cancellationToken)
    {
        var query = _db.ErrorLogs.AsNoTracking();
        var count = await query.LongCountAsync(cancellationToken);
        DateTime? oldest = null;
        DateTime? newest = null;
        if (count > 0)
        {
            oldest = await query.MinAsync(x => x.OccurredOnUtc, cancellationToken);
            newest = await query.MaxAsync(x => x.OccurredOnUtc, cancellationToken);
        }

        return new ErrorLogStorageDto(
            count, await TryGetTableSizeAsync(cancellationToken), oldest, newest, await TryGetDataSizeAsync(count, cancellationToken));
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        cutoffUtc = ClampToFloor(cutoffUtc);
        var total = 0;
        while (true)
        {
            var ids = await _db.ErrorLogs
                .Where(x => x.OccurredOnUtc < cutoffUtc)
                .OrderBy(x => x.OccurredOnUtc)
                .Select(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            // ExecuteDelete is a set-based statement: it does not go through the change tracker, which is where the
            // append-only guard on audit tables lives. This is the one sanctioned way entries leave the log.
            total += await _db.ErrorLogs.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        if (total > 0)
        {
            await _db.ErrorResolutions
                .Where(r => !_db.ErrorLogs.Any(e => e.ErrorReferenceId == r.ErrorReferenceId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        return total;
    }

    public async Task<ErrorLogDeleteResult> DeleteAndReclaimAsync(DateTime? olderThanUtc, CancellationToken cancellationToken)
    {
        var provider = _db.Database.ProviderName ?? string.Empty;
        var sqlServer = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        var postgres = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var deleted = 0;
        var reclaimed = false;
        string? note = null;

        if (olderThanUtc is null && _floorDays > 0)
        {
            // With a retention floor "all" means everything OLD ENOUGH to delete; TRUNCATE would also take the recent entries.
            olderThanUtc = DateTime.UtcNow;
            note = $"Entries from the last {_floorDays} day(s) are kept (retention floor).";
        }

        if (olderThanUtc is null)
        {
            // Everything: TRUNCATE hands the table's pages straight back (a row-by-row delete would not) and is near-instant
            // however large the table is. The table is locked, counted and truncated in ONE transaction, so the number
            // reported is exactly the number destroyed - nothing can slip in between. Falls back to batched deletes if the
            // login may not truncate.
            var table = sqlServer ? "[ErrorLogs]" : postgres ? "\"ErrorLogs\"" : null;
            var truncated = false;
            if (table is not null)
            {
                try
                {
                    await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
                    // Give up quickly rather than queue in front of the writers if the table is busy.
                    await _db.Database.ExecuteSqlRawAsync(sqlServer ? "SET LOCK_TIMEOUT 5000" : "SET LOCAL lock_timeout = '5s'", cancellationToken);
                    long count;
                    if (sqlServer)
                    {
                        count = (await _db.Database
                            .SqlQueryRaw<long>("SELECT COUNT_BIG(*) AS [Value] FROM [ErrorLogs] WITH (TABLOCKX, HOLDLOCK)")
                            .ToListAsync(cancellationToken)).Single();
                    }
                    else
                    {
                        await _db.Database.ExecuteSqlRawAsync("LOCK TABLE \"ErrorLogs\" IN ACCESS EXCLUSIVE MODE", cancellationToken);
                        count = (await _db.Database
                            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"ErrorLogs\"")
                            .ToListAsync(cancellationToken)).Single();
                    }

                    await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE " + table, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    truncated = true;
                    reclaimed = true;
                    deleted = count > int.MaxValue ? int.MaxValue : (int)count;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    note = "The table could not be emptied in one step (the login may not truncate, or the table was busy), so entries were deleted one batch at a time.";
                }
            }

            if (!truncated)
            {
                deleted = await DeleteOlderThanAsync(DateTime.UtcNow.AddMinutes(5), cancellationToken);
            }

            await _db.ErrorResolutions.ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            if (_floorDays > 0 && olderThanUtc.Value > DateTime.UtcNow.AddDays(-_floorDays))
            {
                note = $"Entries from the last {_floorDays} day(s) are kept (retention floor).";
            }

            deleted = await DeleteOlderThanAsync(olderThanUtc.Value, cancellationToken);
            if (note is not null)
            {
                await _db.ErrorResolutions
                    .Where(r => !_db.ErrorLogs.Any(e => e.ErrorReferenceId == r.ErrorReferenceId))
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }

        if (!reclaimed && deleted > 0)
        {
            (reclaimed, var reclaimNote) = await TryReclaimSpaceAsync(sqlServer, postgres, cancellationToken);
            note ??= reclaimNote;
        }

        return new ErrorLogDeleteResult(deleted, reclaimed, note);
    }

    /// <summary>Compacts the table online (SQL Server: ALTER INDEX ... REORGANIZE; PostgreSQL: VACUUM) so the pages the deleted
    /// entries used are released for reuse. It does not lock out writers; the file itself may stay the same size.</summary>
    private async Task<(bool Reclaimed, string? Note)> TryReclaimSpaceAsync(bool sqlServer, bool postgres, CancellationToken cancellationToken)
    {
        // Neither statement may stop errors being written meanwhile. SQL Server: REORGANIZE is an online operation (no
        // table lock) that compacts the table and frees emptied pages. PostgreSQL: a plain VACUUM does not lock out
        // writers (VACUUM FULL would). Both are preceded by a short lock timeout, so if anything does conflict the
        // maintenance gives up instead of queueing in front of the writers.
        var sql = sqlServer ? "SET LOCK_TIMEOUT 5000; ALTER INDEX ALL ON [ErrorLogs] REORGANIZE"
            : postgres ? "VACUUM (ANALYZE) \"ErrorLogs\""
            : null;
        if (sql is null)
        {
            return (false, "This database type cannot release the space automatically.");
        }

        try
        {
            var connection = _db.Database.GetDbConnection();
            var wasClosed = connection.State != ConnectionState.Open;
            if (wasClosed)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 300;
                await command.ExecuteNonQueryAsync(cancellationToken);
                return (true, null);
            }
            finally
            {
                if (wasClosed)
                {
                    await connection.CloseAsync();
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (false, "The entries were deleted, but the database login is not allowed to rebuild the table, so the space is reserved until the database reuses it.");
        }
    }

    /// <summary>Allocated size of the ErrorLogs table (data + indexes), or null if this database can't tell us.</summary>
    private Task<long?> TryGetTableSizeAsync(CancellationToken cancellationToken)
    {
        var provider = _db.Database.ProviderName ?? string.Empty;
        string? sql = null;
        if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            sql = "SELECT SUM(a.total_pages) * 8192 FROM sys.tables t " +
                  "JOIN sys.indexes i ON t.object_id = i.object_id " +
                  "JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id " +
                  "JOIN sys.allocation_units a ON p.partition_id = a.container_id " +
                  "WHERE t.name = 'ErrorLogs'";
        }
        else if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            sql = "SELECT pg_total_relation_size('\"ErrorLogs\"')";
        }

        return ScalarAsync(sql, cancellationToken);
    }

    // Per-row allowance for the fixed-size columns (ids, timestamps, short strings) and index entries.
    private const int PerRowOverheadBytes = 400;

    /// <summary>Approximate size of what is actually stored: the text columns' lengths plus a per-row allowance. Unlike
    /// the allocated size, this drops when entries are deleted.</summary>
    private async Task<long?> TryGetDataSizeAsync(long entryCount, CancellationToken cancellationToken)
    {
        var provider = _db.Database.ProviderName ?? string.Empty;
        string? sql = null;
        if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            sql = "SELECT ISNULL(SUM(CAST(ISNULL(DATALENGTH([Message]),0) + ISNULL(DATALENGTH([StackTrace]),0) + " +
                  "ISNULL(DATALENGTH([UserFriendlyMessage]),0) + ISNULL(DATALENGTH([DiagnosisCause]),0) + " +
                  "ISNULL(DATALENGTH([ExceptionType]),0) AS bigint)), 0) FROM [ErrorLogs]";
        }
        else if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            sql = "SELECT COALESCE(SUM(COALESCE(octet_length(\"Message\"),0) + COALESCE(octet_length(\"StackTrace\"),0) + " +
                  "COALESCE(octet_length(\"UserFriendlyMessage\"),0) + COALESCE(octet_length(\"DiagnosisCause\"),0) + " +
                  "COALESCE(octet_length(\"ExceptionType\"),0)), 0) FROM \"ErrorLogs\"";
        }

        var text = await ScalarAsync(sql, cancellationToken);
        return text is null ? null : text + entryCount * PerRowOverheadBytes;
    }

    private async Task<long?> ScalarAsync(string? sql, CancellationToken cancellationToken)
    {
        if (sql is null)
        {
            return null;
        }

        try
        {
            var connection = _db.Database.GetDbConnection();
            var wasClosed = connection.State != ConnectionState.Open;
            if (wasClosed)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                var result = await command.ExecuteScalarAsync(cancellationToken);
                return result is null or DBNull ? null : Convert.ToInt64(result);
            }
            finally
            {
                if (wasClosed)
                {
                    await connection.CloseAsync();
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No permission to read the catalog views, or an unsupported engine: the screen just omits the figure.
            return null;
        }
    }
}

/// <summary>
/// Applies the "auto-clear" setting: once an hour, when enabled, deletes error-log entries older than the configured
/// number of days. Hosted by both the Api and the Worker; the delete is idempotent so running in both is harmless.
/// </summary>
public sealed class ErrorLogRetentionWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IErrorCapturePolicy _policy;
    private readonly ILogger<ErrorLogRetentionWorker> _logger;

    public ErrorLogRetentionWorker(
        IServiceScopeFactory scopeFactory, IErrorCapturePolicy policy, ILogger<ErrorLogRetentionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _policy = policy;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _policy.RefreshAsync(cancellationToken);
            var settings = _policy.Current;
            if (!settings.AutoClearEnabled)
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IErrorLogMaintenanceService>();
            var deleted = await maintenance.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-settings.RetentionDays), cancellationToken);
            if (deleted > 0)
            {
                // Removing log entries is itself an auditable act: leave a record of how much, and under which setting.
                var governanceLogger = scope.ServiceProvider.GetRequiredService<IGovernanceLogger>();
                await governanceLogger.LogSecurityEventAsync(
                    new SecurityEventEntry(
                        "ErrorLogAutoCleared", "Information", null,
                        $"Auto-clear removed {deleted} error log entries older than {settings.RetentionDays} day(s)."),
                    cancellationToken);

                _logger.LogInformation(
                    "Error log auto-clear removed {Count} entries older than {Days} day(s).", deleted, settings.RetentionDays);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Error log auto-clear failed; it will retry on the next cycle.");
        }
    }
}
