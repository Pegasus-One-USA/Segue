using System.Net.Http.Headers;
using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records as NDJSON to Amazon S3 (or any S3-compatible store) via a pre-signed URL — mirroring the
/// blob-storage writer's URL-based delivery and avoiding a hard AWS SDK dependency. The destination secret holds the
/// pre-signed PUT base URL (or a local directory for dev). For SigV4-signed direct uploads, point the secret at a
/// pre-signed URL issued by the caller.
/// </summary>
public sealed class MappedS3DestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public MappedS3DestinationWriter(ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
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
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "ndjson");
        var content = MappedDestinationSerialization.ToNdjson(records);

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var client = _httpClientFactory.CreateClient(nameof(MappedS3DestinationWriter));
            var endpoint = $"{target.TrimEnd('/')}/{Uri.EscapeDataString(fileName)}";
            using var body = new StringContent(content, Encoding.UTF8);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            using var response = await client.PutAsync(endpoint, body, cancellationToken);
            response.EnsureSuccessStatusCode();
            return records.Count;
        }

        // Local/dev fallback.
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, fileName), content, cancellationToken);
        return records.Count;
    }
}
