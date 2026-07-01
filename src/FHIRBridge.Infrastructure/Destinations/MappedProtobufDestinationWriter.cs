using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records as length-delimited Protobuf messages using the documented FHIRBridgeMappedRecord schema.
/// A matching .proto sidecar is emitted next to the data file so Databricks, Spark, or downstream services can bind it.
/// </summary>
public sealed class MappedProtobufDestinationWriter : IConfiguredDestinationWriter
{
    private const string ProtoSchema = """
        syntax = "proto3";
        package fhirbridge.destinations;

        message FHIRBridgeMappedRecord {
          string tenant_id = 1;
          string pipeline_run_id = 2;
          string resource_type = 3;
          string destination_object = 4;
          string source_resource_id = 5;
          string written_on_utc = 6;
          map<string, string> values = 7;
        }
        """;

    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedProtobufDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<int> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        var target = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "pb");

        using var buffer = new MemoryStream();
        foreach (var record in records)
        {
            var message = EncodeRecord(record);
            WriteVarint(buffer, (ulong)message.Length);
            await buffer.WriteAsync(message, cancellationToken);
        }

        var httpClient = _httpClientFactory.CreateClient(nameof(MappedProtobufDestinationWriter));
        await MappedDestinationSerialization.WriteBinaryTargetAsync(target, fileName, buffer.ToArray(), httpClient, cancellationToken);

        // Emit the matching .proto sidecar so downstream consumers can bind the schema.
        await MappedDestinationSerialization.WriteTextTargetAsync(
            target, "fhirbridge_mapped_record.proto", ProtoSchema, httpClient, cancellationToken);

        return records.Count;
    }

    private static byte[] EncodeRecord(MappedDestinationRecord record)
    {
        using var stream = new MemoryStream();
        WriteStringField(stream, 1, record.TenantId.ToString());
        WriteStringField(stream, 2, record.PipelineRunId.ToString());
        WriteStringField(stream, 3, record.ResourceType);
        WriteStringField(stream, 4, record.DestinationObject);
        WriteStringField(stream, 5, record.SourceResourceId ?? string.Empty);
        WriteStringField(stream, 6, DateTime.UtcNow.ToString("o"));

        foreach (var (key, value) in record.Values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            using var entry = new MemoryStream();
            WriteStringField(entry, 1, key ?? string.Empty);
            WriteStringField(entry, 2, value?.ToString() ?? string.Empty);
            WriteLengthDelimitedField(stream, 7, entry.ToArray());
        }

        return stream.ToArray();
    }

    private static void WriteStringField(Stream stream, int fieldNumber, string value)
        => WriteLengthDelimitedField(stream, fieldNumber, Encoding.UTF8.GetBytes(value));

    private static void WriteLengthDelimitedField(Stream stream, int fieldNumber, byte[] value)
    {
        WriteVarint(stream, (ulong)((fieldNumber << 3) | 2));
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[10];
        var index = 0;
        while (value >= 0x80)
        {
            buffer[index++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[index++] = (byte)value;
        stream.Write(buffer[..index]);
    }
}
