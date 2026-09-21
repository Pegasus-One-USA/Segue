using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// Session-scoped DB advisory lock that serializes startup migrations/seeding across every process that
/// calls <see cref="TryAcquire"/> against the same database — the Api host (which also runs the RBAC
/// bootstrapper and the endpoint-directory/system-settings seeders) and the Worker host (which only runs
/// <c>Database.Migrate()</c>). Both hosts can start at the same instant during a deploy or a replica
/// scale-out, and every one of those startup steps is a "check if it's already there, then insert"
/// operation that is only safe against one runner at a time. Whichever caller starts second blocks in
/// <see cref="TryAcquire"/> until the first releases it (by disposing the returned lock or by its
/// connection dropping, e.g. a crash), then finds everything already migrated/seeded so its own pass is a
/// no-op.
/// </summary>
public static class DatabaseBootstrapLock
{
    private const long PostgresLockKey = 872346123991L; // arbitrary constant, unique to this app's bootstrap step
    private const string SqlServerLockResource = "FHIRBridge:DatabaseBootstrap";

    /// <summary>
    /// Blocks until the lock is acquired and returns an <see cref="IDisposable"/> that releases it, or
    /// <c>null</c> when locking isn't applicable — an unrecognized provider (e.g. InMemory, used by tests),
    /// or a brand-new install whose target database doesn't exist yet (see remarks below).
    /// </summary>
    public static IDisposable? TryAcquire(FHIRBridgeDbContext dbContext, ILogger logger)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        var isNpgsql = providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var isSqlServer = providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        if (!isNpgsql && !isSqlServer)
        {
            return null;
        }

        // Opening the connection here (rather than letting the first EF command open/close it implicitly)
        // keeps it open for the lifetime of the returned lock — a session-scoped lock is only as good as
        // the session it's held on, and the caller's later Migrate()/seeder calls reuse this same open
        // connection since they share this same DbContext.
        try
        {
            dbContext.Database.OpenConnection();
        }
        catch (Exception exception)
        {
            // On a genuinely brand-new install the server exists but the target database does not yet —
            // Database.Migrate() creates it (that first-run path predates this lock and must keep working),
            // but a plain ADO connection open fails immediately against a database that isn't there. Skip
            // the lock rather than block that first boot; the narrow race this leaves — two replicas racing
            // to create/migrate a database that doesn't exist yet — is the same one that already existed
            // before this lock was introduced, not a new one.
            logger.LogWarning(
                exception,
                "Could not open a connection to acquire the database bootstrap lock (expected on a brand-new "
                + "install whose database doesn't exist yet) — proceeding without it.");
            return null;
        }

        try
        {
            if (isNpgsql)
            {
                dbContext.Database.ExecuteSqlInterpolated($"SELECT pg_advisory_lock({PostgresLockKey})");
            }
            else
            {
                // sp_getapplock signals failure (timeout/deadlock-victim/parameter error) via its integer
                // return code, not by throwing — EXEC alone discards that return code, so it must be
                // captured and turned into a real error, otherwise a failed acquisition looks identical to
                // a successful one and two callers could both believe they hold the lock.
                dbContext.Database.ExecuteSqlInterpolated($@"
DECLARE @lockResult int;
EXEC @lockResult = sp_getapplock @Resource = {SqlServerLockResource}, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = -1;
IF @lockResult < 0
BEGIN
    RAISERROR('sp_getapplock failed to acquire the database bootstrap lock (return code %d).', 16, 1, @lockResult);
END");
            }
        }
        catch
        {
            dbContext.Database.CloseConnection();
            throw;
        }

        logger.LogInformation("Acquired database bootstrap lock.");
        return new Lock(dbContext, isNpgsql, PostgresLockKey, SqlServerLockResource, logger);
    }

    private sealed class Lock : IDisposable
    {
        private readonly FHIRBridgeDbContext _dbContext;
        private readonly bool _isNpgsql;
        private readonly long _postgresLockKey;
        private readonly string _sqlServerLockResource;
        private readonly ILogger _logger;

        public Lock(
            FHIRBridgeDbContext dbContext, bool isNpgsql, long postgresLockKey, string sqlServerLockResource,
            ILogger logger)
        {
            _dbContext = dbContext;
            _isNpgsql = isNpgsql;
            _postgresLockKey = postgresLockKey;
            _sqlServerLockResource = sqlServerLockResource;
            _logger = logger;
        }

        public void Dispose()
        {
            try
            {
                if (_isNpgsql)
                {
                    _dbContext.Database.ExecuteSqlInterpolated($"SELECT pg_advisory_unlock({_postgresLockKey})");
                }
                else
                {
                    _dbContext.Database.ExecuteSqlInterpolated(
                        $"EXEC sp_releaseapplock @Resource = {_sqlServerLockResource}, @LockOwner = 'Session'");
                }
            }
            catch (Exception exception)
            {
                // Non-fatal: the connection close right below this releases a session-scoped lock regardless.
                _logger.LogWarning(exception, "Failed to explicitly release the database bootstrap lock.");
            }
            finally
            {
                _dbContext.Database.CloseConnection();
            }
        }
    }
}
