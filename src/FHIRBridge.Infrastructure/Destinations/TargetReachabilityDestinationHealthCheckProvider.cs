using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Covers every destination type whose writer delivers via <see cref="MappedDestinationSerialization"/>'s
/// local-disk-or-presigned-URL convention (Blob Storage, CSV, Excel, PowerBI, Snowflake, S3, NDJSON, Parquet,
/// Tableau, PDF, Avro, Protobuf) — one reusable implementation registered once per applicable
/// <see cref="DestinationType"/>, rather than 12 near-identical classes. Resolves the destination's secret (the
/// same target string its writer PUTs to) and either HEAD-requests it (http/https target) or verifies the
/// local/mounted directory is reachable.
/// </summary>
public sealed class TargetReachabilityDestinationHealthCheckProvider : IDestinationHealthCheckProvider
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public TargetReachabilityDestinationHealthCheckProvider(
        DestinationType destinationType, ISecretProvider secretProvider, IHttpClientFactory httpClientFactory)
    {
        DestinationType = destinationType;
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
    }

    public DestinationType DestinationType { get; }

    public async Task<DestinationHealthCheckResult> CheckAsync(
        DestinationConfiguration destination, CancellationToken cancellationToken)
    {
        string target;
        try
        {
            target = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        }
        catch (Exception exception)
        {
            return new DestinationHealthCheckResult(false, $"Could not resolve target: {exception.Message}");
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                var client = _httpClientFactory.CreateClient(nameof(TargetReachabilityDestinationHealthCheckProvider));
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = await client.SendAsync(request, cancellationToken);
                return new DestinationHealthCheckResult((int)response.StatusCode < 500, $"HTTP {(int)response.StatusCode}");
            }
            catch (Exception exception)
            {
                return new DestinationHealthCheckResult(false, exception.Message);
            }
        }

        try
        {
            Directory.CreateDirectory(target);
            return new DestinationHealthCheckResult(true, "Local/mounted path reachable");
        }
        catch (Exception exception)
        {
            return new DestinationHealthCheckResult(false, exception.Message);
        }
    }
}
