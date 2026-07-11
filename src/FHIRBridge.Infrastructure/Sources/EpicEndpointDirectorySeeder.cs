using System.Text.Json;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Integration.Fhir;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Fetches Epic's public FHIR endpoint directory (a paginated FHIR Bundle of Endpoint resources) and imports any
/// entries not already present into the shared EhrEndpoints table, tagged Vendor = Epic and FormatType = "R4" — the
/// only format this directory publishes today. Scopes its "already seeded" check to Epic's own rows, since other
/// vendors' IEhrEndpointDirectorySeeder implementations share this same table.
/// </summary>
public sealed class EpicEndpointDirectorySeeder : IEhrEndpointDirectorySeeder
{
    private const string DirectoryUrl = "https://open.epic.com/Endpoints/R4";
    private const SourceSystemType Vendor = SourceSystemType.Epic;

    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EpicEndpointDirectorySeeder> _logger;

    public EpicEndpointDirectorySeeder(
        FHIRBridgeDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        ILogger<EpicEndpointDirectorySeeder> logger)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Never throws: this runs on every boot alongside IRbacBootstrapper, and a slow/unreachable open.epic.com must
    // not block application startup. A failed attempt is logged and simply retried on the next boot (Epic's rows
    // stay absent, so EnsureAsync doesn't short-circuit next time).
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        try
        {
            var alreadySeeded = await _dbContext.EhrEndpoints
                .IgnoreQueryFilters()
                .AnyAsync(x => x.Vendor == Vendor, cancellationToken);
            if (alreadySeeded)
            {
                return;
            }

            var httpClient = _httpClientFactory.CreateClient(nameof(EpicEndpointDirectorySeeder));
            var imported = 0;
            string? pageUrl = DirectoryUrl;

            while (pageUrl is not null)
            {
                var rawJson = await httpClient.GetStringAsync(pageUrl, cancellationToken);
                foreach (var envelope in FhirResourceParser.ParseSearchBundle(rawJson))
                {
                    if (!string.Equals(envelope.ResourceType, "Endpoint", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var endpoint = ToEhrEndpoint(envelope.ResourceId, envelope.RawJson);
                    if (endpoint is null)
                    {
                        continue;
                    }

                    _dbContext.EhrEndpoints.Add(endpoint);
                    imported++;
                }

                pageUrl = FhirResourceParser.GetNextLink(rawJson);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Imported {Count} Epic endpoint(s) from {DirectoryUrl}.", imported, DirectoryUrl);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to import Epic's endpoint directory from {DirectoryUrl}; will retry on next boot.",
                DirectoryUrl);
        }
    }

    private static EhrEndpoint? ToEhrEndpoint(string resourceId, string rawJson)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        var address = GetString(root, "address");
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var name = GetString(root, "name") ?? resourceId;
        var status = GetString(root, "status") ?? "unknown";

        return new EhrEndpoint(Vendor, resourceId, name, address, "R4", status);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
