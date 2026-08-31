using System.Net.Http.Json;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Shared PUT-with-retry-and-verify logic for the final "load this CodeSystem into the terminology
/// server" step every Hapi*TerminologySyncService performs. Extracted after a real incident: NDC's
/// PUT actually succeeded server-side (confirmed via a follow-up GET showing the correct count and a
/// fresh meta.lastUpdated) but the client-side HttpClient never received the response and gave up
/// after its full timeout, marking the run "Failed" even though the data had already landed.
///
/// On a timeout/network exception this now (1) checks whether the write actually applied before
/// concluding anything failed, and (2) only re-sends the (potentially large) payload if that check
/// shows it genuinely didn't — avoiding a second multi-minute upload attempt when a quick GET can
/// settle the question.
/// </summary>
public sealed class HapiTerminologyServerClient
{
    private readonly IConfiguration _configuration;
    private readonly ISystemSettingsCache _settings;
    private readonly ILogger<HapiTerminologyServerClient> _logger;

    public HapiTerminologyServerClient(IConfiguration configuration, ISystemSettingsCache settings, ILogger<HapiTerminologyServerClient> logger)
    {
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    /// <param name="resourceId">The CodeSystem's fixed id, e.g. "cvx-full".</param>
    /// <param name="resource">The CodeSystem resource body to PUT.</param>
    /// <param name="expectedCount">The resource's own "count" field — used to confirm a verify-GET
    /// reflects this run specifically, not a stale prior load.</param>
    /// <param name="timeout">Per-attempt timeout — kept caller-configurable since payload size varies
    /// enormously across the 13 systems (UCUM's few hundred units vs. SNOMED's ~400k concepts).</param>
    public async Task PutCodeSystemAsync(
        string resourceId, object resource, int expectedCount, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var configuredDefault = _configuration["Terminology:BaseUrl"] ?? "http://hapi-terminology:8080/fhir";
        var serverBaseUrl = (await _settings.GetStringAsync("Terminology:BaseUrl", configuredDefault, cancellationToken)).TrimEnd('/');

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                // Bypasses IHttpClientFactory deliberately: the app-wide resilience default (30s/attempt)
                // would abort large uploads long before they'd naturally complete.
                using var client = new HttpClient { BaseAddress = new Uri(serverBaseUrl + "/"), Timeout = timeout };
                var response = await client.PutAsJsonAsync($"CodeSystem/{resourceId}", resource, cancellationToken);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception exception) when (exception is TaskCanceledException or HttpRequestException)
            {
                _logger.LogWarning(exception,
                    "Attempt {Attempt} to PUT CodeSystem/{ResourceId} failed client-side; checking whether it actually applied before treating this as a real failure.",
                    attempt, resourceId);

                if (await WasActuallyAppliedAsync(serverBaseUrl, resourceId, expectedCount, cancellationToken))
                {
                    _logger.LogInformation(
                        "CodeSystem/{ResourceId} was confirmed applied server-side despite the client-side error on attempt {Attempt} — treating as success.",
                        resourceId, attempt);
                    return;
                }

                if (attempt == 2)
                {
                    throw;
                }
                // Genuinely didn't apply — worth one real retry of the full upload.
            }
        }
    }

    private async Task<bool> WasActuallyAppliedAsync(string serverBaseUrl, string resourceId, int expectedCount, CancellationToken cancellationToken)
    {
        try
        {
            using var verifyClient = new HttpClient { BaseAddress = new Uri(serverBaseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
            using var response = await verifyClient.GetAsync($"CodeSystem/{resourceId}?_summary=true", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return document.RootElement.TryGetProperty("count", out var countElement) && countElement.GetInt32() == expectedCount;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Verification GET for CodeSystem/{ResourceId} itself failed — treating the original attempt as failed.", resourceId);
            return false;
        }
    }
}
