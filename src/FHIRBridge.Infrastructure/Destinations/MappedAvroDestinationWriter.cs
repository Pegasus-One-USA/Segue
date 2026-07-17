using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records as an Avro object container file with nullable string fields. The destination secret holds
/// either an output directory or a pre-signed blob/object PUT URL (Azure Blob / S3) for direct upload.
/// </summary>
public sealed class MappedAvroDestinationWriter : IConfiguredDestinationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedAvroDestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new DestinationWriteResult(0);
        }

        var target = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var columns = MappedDestinationSerialization.GetColumns(records);
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "avro");

        using var buffer = new MemoryStream();
        await WriteObjectContainerAsync(buffer, columns, records, cancellationToken);

        await MappedDestinationSerialization.WriteBinaryTargetAsync(
            target, fileName, buffer.ToArray(),
            _httpClientFactory.CreateClient(nameof(MappedAvroDestinationWriter)),
            cancellationToken);

        return new DestinationWriteResult(records.Count);
    }

    private static async Task WriteObjectContainerAsync(
        Stream stream,
        IReadOnlyList<string> columns,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        var schema = BuildSchema(columns);
        var syncMarker = RandomNumberGenerator.GetBytes(16);

        stream.Write("Obj\x01"u8);
        WriteMap(stream, new Dictionary<string, byte[]>
        {
            ["avro.schema"] = Encoding.UTF8.GetBytes(schema),
            ["avro.codec"] = Encoding.UTF8.GetBytes("null")
        });
        await stream.WriteAsync(syncMarker, cancellationToken);

        using var block = new MemoryStream();
        foreach (var record in records)
        {
            foreach (var column in columns)
            {
                var value = MappedDestinationSerialization.GetCell(record, column);
                if (string.IsNullOrEmpty(value))
                {
                    WriteLong(block, 0);
                    continue;
                }

                WriteLong(block, 1);
                WriteString(block, value);
            }
        }

        WriteLong(stream, records.Count);
        WriteLong(stream, block.Length);
        block.Position = 0;
        await block.CopyToAsync(stream, cancellationToken);
        await stream.WriteAsync(syncMarker, cancellationToken);
    }

    private static string BuildSchema(IReadOnlyList<string> columns)
    {
        var fields = columns.Select(column => new
        {
            name = SanitizeName(column),
            type = new object[] { "null", "string" },
            @default = (string?)null
        });

        return JsonSerializer.Serialize(new
        {
            type = "record",
            name = "FHIRBridgeMappedRecord",
            @namespace = "FHIRBridge.Destinations",
            fields
        }, JsonOptions);
    }

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        if (builder.Length == 0 || !char.IsLetter(builder[0]) && builder[0] != '_')
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    private static void WriteMap(Stream stream, IReadOnlyDictionary<string, byte[]> values)
    {
        WriteLong(stream, values.Count);
        foreach (var (key, value) in values)
        {
            WriteString(stream, key);
            WriteBytes(stream, value);
        }

        WriteLong(stream, 0);
    }

    private static void WriteString(Stream stream, string value)
        => WriteBytes(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(Stream stream, byte[] value)
    {
        WriteLong(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteLong(Stream stream, long value)
    {
        var unsigned = (ulong)((value << 1) ^ (value >> 63));
        Span<byte> buffer = stackalloc byte[10];
        var index = 0;
        while ((unsigned & ~0x7FUL) != 0)
        {
            buffer[index++] = (byte)((unsigned & 0x7F) | 0x80);
            unsigned >>= 7;
        }

        buffer[index++] = (byte)unsigned;
        stream.Write(buffer[..index]);
    }
}
