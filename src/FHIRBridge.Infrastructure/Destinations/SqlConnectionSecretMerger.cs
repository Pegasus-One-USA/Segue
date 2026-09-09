using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Splices credentials from an already-stored SQL connection string into a freshly-built one that's missing
/// them, using the same typed ADO.NET connection-string builders <see cref="SqlDestinationSchemaService"/>
/// already uses to build these strings in the first place — they parse and re-serialize just as well as they
/// build, so no hand-rolled parser is needed and the DB-driver packages stay confined to this (Infrastructure)
/// layer, never referenced from <c>ConfigurationService</c> (Application layer) directly.
/// </summary>
public sealed class SqlConnectionSecretMerger : ISqlConnectionSecretMerger
{
    public string? TryInheritCredentials(DestinationType destinationType, string existingSecret, string newSecret)
    {
        try
        {
            return destinationType switch
            {
                DestinationType.PostgreSql => TryInheritPostgres(existingSecret, newSecret),
                DestinationType.MySql => TryInheritMySql(existingSecret, newSecret),
                DestinationType.SqlServer or DestinationType.AzureSql => TryInheritSqlServer(existingSecret, newSecret),
                _ => null,
            };
        }
        catch
        {
            // Either connection string failed to parse — never let this break an otherwise-valid save; the
            // caller falls back to writing newSecret exactly as it was handed in.
            return null;
        }
    }

    private static string? TryInheritPostgres(string existingSecret, string newSecret)
    {
        var existing = new NpgsqlConnectionStringBuilder(existingSecret);
        if (string.IsNullOrEmpty(existing.Password)) return null;

        var updated = new NpgsqlConnectionStringBuilder(newSecret);
        if (!string.IsNullOrEmpty(updated.Password)) return null;

        updated.Password = existing.Password;
        if (string.IsNullOrEmpty(updated.Username) && !string.IsNullOrEmpty(existing.Username))
        {
            updated.Username = existing.Username;
        }
        return updated.ConnectionString;
    }

    private static string? TryInheritMySql(string existingSecret, string newSecret)
    {
        var existing = new MySqlConnectionStringBuilder(existingSecret);
        if (string.IsNullOrEmpty(existing.Password)) return null;

        var updated = new MySqlConnectionStringBuilder(newSecret);
        if (!string.IsNullOrEmpty(updated.Password)) return null;

        updated.Password = existing.Password;
        if (string.IsNullOrEmpty(updated.UserID) && !string.IsNullOrEmpty(existing.UserID))
        {
            updated.UserID = existing.UserID;
        }
        return updated.ConnectionString;
    }

    private static string? TryInheritSqlServer(string existingSecret, string newSecret)
    {
        var updated = new SqlConnectionStringBuilder(newSecret);
        // Active Directory Default (and integrated security generally) has no password concept at all —
        // setting one would be actively wrong, not just unnecessary.
        if (updated.IntegratedSecurity || updated.Authentication != SqlAuthenticationMethod.NotSpecified)
        {
            return null;
        }
        if (!string.IsNullOrEmpty(updated.Password)) return null;

        var existing = new SqlConnectionStringBuilder(existingSecret);
        if (string.IsNullOrEmpty(existing.Password)) return null;

        updated.Password = existing.Password;
        if (string.IsNullOrEmpty(updated.UserID) && !string.IsNullOrEmpty(existing.UserID))
        {
            updated.UserID = existing.UserID;
        }
        return updated.ConnectionString;
    }
}
