using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Resolves the <see cref="IConfiguredDestinationWriter"/> registered for a <see cref="DestinationType"/>.
/// Supported destinations come from injected <see cref="ConfiguredDestinationWriterRegistration"/> entries, so
/// adding a destination only requires a registration in dependency injection — this factory stays closed for modification.
/// </summary>
public sealed class ConfiguredDestinationWriterFactory : IConfiguredDestinationWriterFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyDictionary<DestinationType, Type> _registry;
    private readonly ILoggerFactory _loggerFactory;

    public ConfiguredDestinationWriterFactory(
        IServiceProvider serviceProvider,
        IEnumerable<ConfiguredDestinationWriterRegistration> registrations,
        ILoggerFactory? loggerFactory = null)
    {
        _serviceProvider = serviceProvider;
        _registry = BuildRegistry(registrations);
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IConfiguredDestinationWriter Create(DestinationType destinationType)
    {
        if (!_registry.TryGetValue(destinationType, out var implementationType))
        {
            throw new NotSupportedException(
                $"Destination type '{destinationType}' is not supported by the configured pipeline yet.");
        }

        var writer = (IConfiguredDestinationWriter)_serviceProvider.GetRequiredService(implementationType);

        // Wrapping here rather than in each writer is what makes write telemetry uniform across every destination
        // type, and automatic for any destination registered later. The logger is named for the concrete writer, so
        // SourceContext in Seq still identifies which implementation ran (several types share one writer).
        return new LoggingConfiguredDestinationWriter(writer, _loggerFactory.CreateLogger(implementationType));
    }

    /// <summary>
    /// The configured-pipeline destination writers. Shared by dependency injection and tests so the mapping is
    /// declared in exactly one place. Several destination types intentionally share one writer implementation.
    /// </summary>
    public static IReadOnlyList<ConfiguredDestinationWriterRegistration> DefaultRegistrations { get; } =
    [
        new(DestinationType.InMemory, typeof(MappedInMemoryDestinationWriter)),
        new(DestinationType.SqlServer, typeof(MappedSqlServerDestinationWriter)),
        new(DestinationType.AzureSql, typeof(MappedSqlServerDestinationWriter)),
        new(DestinationType.BlobStorage, typeof(MappedBlobStorageDestinationWriter)),
        new(DestinationType.RestApi, typeof(MappedRestApiDestinationWriter)),
        new(DestinationType.FhirRepository, typeof(MappedFhirRepositoryDestinationWriter)),
        new(DestinationType.Csv, typeof(MappedCsvDestinationWriter)),
        new(DestinationType.Excel, typeof(MappedExcelDestinationWriter)),
        new(DestinationType.Snowflake, typeof(MappedSnowflakeDestinationWriter)),
        new(DestinationType.PowerBi, typeof(MappedPowerBiDestinationWriter)),
        new(DestinationType.PostgreSql, typeof(MappedPostgreSqlDestinationWriter)),
        new(DestinationType.MySql, typeof(MappedMySqlDestinationWriter)),
        new(DestinationType.S3, typeof(MappedS3DestinationWriter)),
        new(DestinationType.Ndjson, typeof(MappedNdjsonDestinationWriter)),
        new(DestinationType.Parquet, typeof(MappedParquetDestinationWriter)),
        new(DestinationType.Sftp, typeof(MappedSftpDestinationWriter)),
        new(DestinationType.Tableau, typeof(MappedTableauDestinationWriter)),
        new(DestinationType.Pdf, typeof(MappedPdfDestinationWriter)),
        new(DestinationType.Avro, typeof(MappedAvroDestinationWriter)),
        new(DestinationType.Protobuf, typeof(MappedProtobufDestinationWriter)),
        new(DestinationType.Databricks, typeof(MappedDatabricksDestinationWriter)),
        new(DestinationType.Mongo, typeof(MappedMongoDestinationWriter)),
        new(DestinationType.Medplum, typeof(MappedMedplumDestinationWriter)),
        new(DestinationType.AzureFhirService, typeof(MappedFhirRepositoryDestinationWriter)),
        new(DestinationType.DataLakeWebhook, typeof(MappedDataLakeWebhookDestinationWriter)),
        new(DestinationType.DataFabricAzure, typeof(MappedDataFabricDestinationWriter))
    ];

    private static IReadOnlyDictionary<DestinationType, Type> BuildRegistry(
        IEnumerable<ConfiguredDestinationWriterRegistration> registrations)
    {
        var registry = new Dictionary<DestinationType, Type>();

        foreach (var registration in registrations)
        {
            if (!registry.TryAdd(registration.DestinationType, registration.ImplementationType))
            {
                throw new InvalidOperationException(
                    $"More than one configured destination writer is registered for destination type '{registration.DestinationType}'.");
            }
        }

        return registry;
    }
}

/// <summary>
/// Associates a <see cref="DestinationType"/> with the concrete <see cref="IConfiguredDestinationWriter"/> type that handles it.
/// </summary>
public sealed record ConfiguredDestinationWriterRegistration(DestinationType DestinationType, Type ImplementationType);
