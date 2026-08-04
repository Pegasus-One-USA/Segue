using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes governed/de-identified FHIR resources to an Azure Health Data Services FHIR service as a single
/// <c>type: "batch"</c> Bundle (conditional <c>PUT {ResourceType}/{id}</c> per entry — update-or-create, safe to
/// retry) rather than one request per resource, since AHDS enforces per-request throttling more aggressively than
/// a generic FHIR endpoint. A 429 response retries with backoff honoring <c>Retry-After</c> when present.
/// </summary>
public sealed class MappedAzureHealthDataServicesDestinationWriter : IConfiguredDestinationWriter
{
    private const int MaxAttempts = 3;

    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAzureHealthDataServicesTokenProvider _tokenProvider;
    private readonly ILogger<MappedAzureHealthDataServicesDestinationWriter> _logger;

    public MappedAzureHealthDataServicesDestinationWriter(
        ISecretProvider secretProvider,
        IHttpClientFactory httpClientFactory,
        IAzureHealthDataServicesTokenProvider tokenProvider,
        ILogger<MappedAzureHealthDataServicesDestinationWriter> logger)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _logger = logger;
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

        var options = AzureHealthDataServicesConnectionOptions.Parse(destination);

        string? clientSecret = null;
        if (!options.IsManagedIdentity)
        {
            clientSecret = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        }

        var accessToken = await _tokenProvider.GetAccessTokenAsync(options, clientSecret, cancellationToken);
        var httpClient = _httpClientFactory.CreateClient(nameof(MappedAzureHealthDataServicesDestinationWriter));
        var baseUrl = options.FhirServiceUrl.TrimEnd('/');

        var entries = new List<(MappedDestinationRecord Record, string ResourceType, string ResourceId)>(records.Count);
        var bundleJson = BuildBatchBundle(records, entries);

        using var response = await SendWithRetryAsync(httpClient, baseUrl, bundleJson, accessToken, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Azure Health Data Services batch write returned {(int)response.StatusCode} ({response.ReasonPhrase}). {body}");
        }

        return ParseBatchResponse(body, entries);
    }

    private static string BuildBatchBundle(
        IReadOnlyCollection<MappedDestinationRecord> records,
        List<(MappedDestinationRecord Record, string ResourceType, string ResourceId)> entries)
    {
        var bundleEntries = new JsonArray();

        foreach (var record in records)
        {
            var (resourceType, resourceId, resourceJson) = MappedFhirResourceBuilder.Build(record);
            entries.Add((record, resourceType, resourceId));

            bundleEntries.Add(new JsonObject
            {
                ["resource"] = JsonNode.Parse(resourceJson),
                ["request"] = new JsonObject
                {
                    ["method"] = "PUT",
                    ["url"] = $"{resourceType}/{resourceId}"
                }
            });
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "batch",
            ["entry"] = bundleEntries
        };

        return bundle.ToJsonString();
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient httpClient,
        string baseUrl,
        string bundleJson,
        string accessToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(bundleJson, Encoding.UTF8, "application/fhir+json");

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= MaxAttempts)
            {
                return response;
            }

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            _logger.LogWarning(
                "Azure Health Data Services throttled the batch write (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}.",
                attempt,
                MaxAttempts,
                delay);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static DestinationWriteResult ParseBatchResponse(
        string body,
        List<(MappedDestinationRecord Record, string ResourceType, string ResourceId)> entries)
    {
        var responseEntries = (JsonNode.Parse(body) as JsonObject)?["entry"] as JsonArray;
        if (responseEntries is null || responseEntries.Count != entries.Count)
        {
            // Server didn't echo one response entry per submitted entry — treat the whole batch as written since
            // the outer call already checked IsSuccessStatusCode.
            return new DestinationWriteResult(entries.Count);
        }

        var errors = new List<string>();
        var writtenIds = new List<string?>();
        var successCount = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            var status = responseEntries[i]?["response"]?["status"]?.GetValue<string>();
            if (status is not null && status.StartsWith("2", StringComparison.Ordinal))
            {
                successCount++;
                writtenIds.Add(entries[i].Record.SourceResourceId ?? entries[i].ResourceId);
            }
            else
            {
                errors.Add($"{entries[i].ResourceType}/{entries[i].ResourceId}: {status ?? "unknown status"}");
            }
        }

        return new DestinationWriteResult(
            successCount,
            RecordErrors: errors.Count > 0 ? errors : null,
            WrittenResourceIds: writtenIds);
    }
}
