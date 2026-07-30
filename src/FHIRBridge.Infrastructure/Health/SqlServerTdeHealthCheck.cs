using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FHIRBridge.Infrastructure.Health;

/// <summary>
/// Verifies the database is protected by Transparent Data Encryption (encryption at rest, HIPAA
/// §164.312(a)(2)(iv)). TDE is configured at the SQL Server / Azure SQL layer, not in application
/// code; this check surfaces whether it is actually on. By default an unencrypted database reports
/// Degraded (visible but non-fatal); set <c>Compliance:RequireTde=true</c> to make it Unhealthy so
/// deployment/readiness probes fail closed.
/// </summary>
public sealed class SqlServerTdeHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settingsCache;

    public SqlServerTdeHealthCheck(IConfiguration configuration, ISystemSettingsCache settingsCache)
    {
        _configuration = configuration;
        _settingsCache = settingsCache;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _configuration.GetConnectionString("FHIRBridgeDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Degraded("FHIRBridgeDb is not configured; TDE status cannot be verified.");
        }

        var requireTde = await _settingsCache.GetBoolAsync(
            "Compliance:RequireTde", _configuration.GetValue<bool>("Compliance:RequireTde"), cancellationToken);
        Func<string, HealthCheckResult> notEncrypted = requireTde
            ? reason => HealthCheckResult.Unhealthy(reason)
            : reason => HealthCheckResult.Degraded(reason);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // encryption_state = 3 => encrypted. NULL join => no encryption key => TDE off.
            const string sql = @"
                SELECT ISNULL(k.encryption_state, 0)
                FROM sys.databases d
                LEFT JOIN sys.dm_database_encryption_keys k ON k.database_id = d.database_id
                WHERE d.name = DB_NAME();";

            await using var command = new SqlCommand(sql, connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            var encryptionState = result is int state ? state : Convert.ToInt32(result ?? 0);

            return encryptionState == 3
                ? HealthCheckResult.Healthy("Database is encrypted at rest (TDE active).")
                : notEncrypted($"Database is NOT encrypted at rest (TDE encryption_state={encryptionState}). Enable Transparent Data Encryption.");
        }
        catch (Exception exception)
        {
            // Lacking VIEW DATABASE STATE (e.g. least-privilege app login) shouldn't fail the app;
            // report Degraded so the gap is visible without taking the service down.
            return HealthCheckResult.Degraded("Unable to verify TDE status (insufficient permission or query error).", exception);
        }
    }
}
