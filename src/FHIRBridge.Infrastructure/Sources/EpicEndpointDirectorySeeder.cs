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
/// Seeds two kinds of Epic rows into the shared EhrEndpoints table (Vendor = Epic, FormatType = "R4" — the only
/// format the live directory publishes today): the single generic App Orchard / interconnect FHIR sandbox
/// (EndpointType.Epic), and every entry from Epic's public FHIR endpoint directory — a paginated FHIR Bundle of
/// Endpoint resources, each a specific customer's own production MyChart instance (EndpointType.MyChart). Each has
/// its own "already seeded" check scoped by EndpointType, since other vendors' IEhrEndpointDirectorySeeder
/// implementations share this same table.
/// </summary>
public sealed class EpicEndpointDirectorySeeder : IEhrEndpointDirectorySeeder
{
    private const string DirectoryUrl = "https://open.epic.com/Endpoints/R4";
    private const SourceSystemType Vendor = SourceSystemType.Epic;

    // The generic App Orchard / interconnect FHIR sandbox — a single shared test system, not any specific
    // customer's production MyChart instance (which is what the live directory below imports).
    private const string SandboxVendorEndpointId = "epic-sandbox";
    private const string SandboxFhirBaseUrl = "https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4";

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
    // stay absent, so EnsureAsync doesn't short-circuit next time). The sandbox row is independent of the live
    // directory import below — each has its own idempotency check, scoped by EndpointType, so seeding one doesn't
    // short-circuit the other.
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await EnsureSandboxEndpointAsync(cancellationToken);
        await EnsureMyChartDirectoryAsync(cancellationToken);
    }

    private async Task EnsureSandboxEndpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            var alreadySeeded = await _dbContext.EhrEndpoints
                .IgnoreQueryFilters()
                .AnyAsync(x => x.Vendor == Vendor && x.EndpointType == EhrEndpointType.Epic, cancellationToken);
            if (alreadySeeded)
            {
                return;
            }

            _dbContext.EhrEndpoints.Add(new EhrEndpoint(
                Vendor,
                SandboxVendorEndpointId,
                "Epic FHIR Sandbox (App Orchard / Interconnect)",
                SandboxFhirBaseUrl,
                "R4",
                "active",
                EhrEndpointType.Epic));

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Seeded the Epic FHIR sandbox endpoint ({FhirBaseUrl}).", SandboxFhirBaseUrl);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to seed the Epic FHIR sandbox endpoint; will retry on next boot.");
        }
    }

    private async Task EnsureMyChartDirectoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var alreadySeeded = await _dbContext.EhrEndpoints
                .IgnoreQueryFilters()
                .AnyAsync(x => x.Vendor == Vendor && x.EndpointType == EhrEndpointType.MyChart, cancellationToken);
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

    // Every row from the live directory is a specific customer's own production MyChart instance, as opposed to
    // the shared sandbox seeded above.
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

        return new EhrEndpoint(Vendor, resourceId, name, address, "R4", status, EhrEndpointType.MyChart);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
