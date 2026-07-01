using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FHIRBridge.Infrastructure.Health;

public sealed class SqlServerConnectionHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public SqlServerConnectionHealthCheck(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _configuration.GetConnectionString("FHIRBridgeDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Degraded("FHIRBridgeDb is not configured; in-memory repositories are active.");
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("SQL Server connection is healthy.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("SQL Server connection failed.", exception);
        }
    }
}
