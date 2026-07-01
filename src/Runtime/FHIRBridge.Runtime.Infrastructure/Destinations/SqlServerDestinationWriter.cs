using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Integration.Sql;
using FHIRBridge.Runtime.Application.Abstractions.Destinations;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Entities;
using FHIRBridge.Runtime.Domain.ValueObjects;
using Microsoft.Data.SqlClient;

namespace FHIRBridge.Runtime.Infrastructure.Destinations;

public sealed class SqlServerDestinationWriter : IDestinationWriter
{
    public async Task<int> WriteAsync(
        PipelineRun pipelineRun,
        RuntimeDestinationConfiguration destination,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination.ConnectionString))
        {
            throw new InvalidOperationException("SQL Server destination connection string is required.");
        }

        var schemaName = ValidateSchemaName(destination.SchemaName);

        await using var connection = await SqlServerConnectionFactory.OpenConnectionAsync(destination.ConnectionString, cancellationToken);

        await EnsureSchemaAsync(connection, schemaName, cancellationToken);

        foreach (var resource in resources)
        {
            await InsertResourceAsync(connection, schemaName, pipelineRun, resource, cancellationToken);
            await InsertAuditAsync(connection, schemaName, pipelineRun, resource, cancellationToken);
        }

        return resources.Count;
    }

    private static async Task EnsureSchemaAsync(
        SqlConnection connection,
        string schemaName,
        CancellationToken cancellationToken)
    {
        var createSchemaSql = $"""
            IF SCHEMA_ID(N'{schemaName}') IS NULL
            BEGIN
                EXEC(N'CREATE SCHEMA [{schemaName}]')
            END
            """;

        await using (var command = new SqlCommand(createSchemaSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var createResourcesSql = $"""
            IF OBJECT_ID(N'[{schemaName}].[FhirResources]', N'U') IS NULL
            BEGIN
                CREATE TABLE [{schemaName}].[FhirResources]
                (
                    Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_{schemaName}_FhirResources PRIMARY KEY,
                    TenantId UNIQUEIDENTIFIER NOT NULL,
                    PipelineRunId UNIQUEIDENTIFIER NOT NULL,
                    ResourceType NVARCHAR(100) NOT NULL,
                    ResourceId NVARCHAR(200) NULL,
                    VersionId NVARCHAR(100) NULL,
                    LastUpdated DATETIMEOFFSET NULL,
                    ResourceHash NVARCHAR(128) NOT NULL,
                    ResourceJson NVARCHAR(MAX) NOT NULL,
                    WrittenOnUtc DATETIME2 NOT NULL
                );

                CREATE INDEX IX_{schemaName}_FhirResources_Run
                    ON [{schemaName}].[FhirResources] (PipelineRunId, ResourceType);
            END
            """;

        await using (var command = new SqlCommand(createResourcesSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var createAuditSql = $"""
            IF OBJECT_ID(N'[{schemaName}].[PipelineResourceAudit]', N'U') IS NULL
            BEGIN
                CREATE TABLE [{schemaName}].[PipelineResourceAudit]
                (
                    AuditId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_{schemaName}_PipelineResourceAudit PRIMARY KEY,
                    TenantId UNIQUEIDENTIFIER NOT NULL,
                    PipelineRunId UNIQUEIDENTIFIER NOT NULL,
                    ResourceType NVARCHAR(100) NOT NULL,
                    ResourceId NVARCHAR(200) NULL,
                    Action NVARCHAR(100) NOT NULL,
                    Message NVARCHAR(500) NOT NULL,
                    OccurredOnUtc DATETIME2 NOT NULL
                );

                CREATE INDEX IX_{schemaName}_PipelineResourceAudit_Run
                    ON [{schemaName}].[PipelineResourceAudit] (PipelineRunId, ResourceType);
            END
            """;

        await using (var command = new SqlCommand(createAuditSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertResourceAsync(
        SqlConnection connection,
        string schemaName,
        PipelineRun pipelineRun,
        ResourceEnvelope resource,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO [{schemaName}].[FhirResources]
            (
                TenantId,
                PipelineRunId,
                ResourceType,
                ResourceId,
                VersionId,
                LastUpdated,
                ResourceHash,
                ResourceJson,
                WrittenOnUtc
            )
            VALUES
            (
                @TenantId,
                @PipelineRunId,
                @ResourceType,
                @ResourceId,
                @VersionId,
                @LastUpdated,
                @ResourceHash,
                @ResourceJson,
                @WrittenOnUtc
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@TenantId", pipelineRun.TenantId);
        command.Parameters.AddWithValue("@PipelineRunId", pipelineRun.Id);
        command.Parameters.AddWithValue("@ResourceType", resource.ResourceType);
        command.Parameters.AddWithValue("@ResourceId", (object?)resource.ResourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@VersionId", (object?)resource.VersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@LastUpdated", (object?)resource.LastUpdated ?? DBNull.Value);
        command.Parameters.AddWithValue("@ResourceHash", ComputeHash(resource.RawJson));
        command.Parameters.AddWithValue("@ResourceJson", resource.RawJson);
        command.Parameters.AddWithValue("@WrittenOnUtc", DateTime.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(
        SqlConnection connection,
        string schemaName,
        PipelineRun pipelineRun,
        ResourceEnvelope resource,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO [{schemaName}].[PipelineResourceAudit]
            (
                AuditId,
                TenantId,
                PipelineRunId,
                ResourceType,
                ResourceId,
                Action,
                Message,
                OccurredOnUtc
            )
            VALUES
            (
                @AuditId,
                @TenantId,
                @PipelineRunId,
                @ResourceType,
                @ResourceId,
                @Action,
                @Message,
                @OccurredOnUtc
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@AuditId", Guid.NewGuid());
        command.Parameters.AddWithValue("@TenantId", pipelineRun.TenantId);
        command.Parameters.AddWithValue("@PipelineRunId", pipelineRun.Id);
        command.Parameters.AddWithValue("@ResourceType", resource.ResourceType);
        command.Parameters.AddWithValue("@ResourceId", (object?)resource.ResourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Action", "ResourceWritten");
        command.Parameters.AddWithValue("@Message", "FHIR resource written to SQL Server output. Payload is stored in FhirResources; audit row contains no clinical content.");
        command.Parameters.AddWithValue("@OccurredOnUtc", DateTime.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ValidateSchemaName(string? schemaName)
    {
        var value = string.IsNullOrWhiteSpace(schemaName) ? "fhirbridge" : schemaName;

        if (value.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException("SQL Server destination schema name can only contain letters, digits, and underscores.");
        }

        return value;
    }

    private static string ComputeHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(hash);
    }
}
