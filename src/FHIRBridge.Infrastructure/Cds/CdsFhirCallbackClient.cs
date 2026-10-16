using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Observability;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>
/// Fetches missing prefetch from the CDS client's own <c>fhirServer</c> with the client's own short-lived
/// <c>fhirAuthorization</c> token. A typed HttpClient whose primary handler refuses private, loopback and link-local
/// addresses (<see cref="CdsJwksHttpHandler.CreateGuarded"/>) and never follows redirects; its HttpClient loggers are
/// removed at registration because request URLs carry patient ids.
/// <para>
/// Never throws for a remote failure, never logs anything (token, URLs and bodies hold PHI), and reports only short
/// fixed failure reasons.
/// </para>
/// </summary>
public sealed class CdsFhirCallbackClient : ICdsFhirCallbackClient
{
    private const string FhirJson = "application/fhir+json";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<CdsOptions> _options;

    public CdsFhirCallbackClient(HttpClient httpClient, IOptionsMonitor<CdsOptions> options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<CdsCallbackResult> FetchAsync(CdsCallbackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var resources = request.Queries.Keys.ToDictionary(key => key, _ => (JsonNode?)null, StringComparer.Ordinal);

        CdsCallbackResult Refused(string reason) => new(resources, 0, stopwatch.ElapsedMilliseconds, reason);

        if (request.Queries.Count == 0)
        {
            return new CdsCallbackResult(resources, 0, 0, null);
        }

        // Second line of defence: the evaluator has already matched fhirServer to the trusted issuer.
        var normalized = CdsTrustedIssuer.NormalizeUrl(request.FhirServer);
        if (normalized is null || normalized != CdsTrustedIssuer.NormalizeUrl(request.ExpectedFhirBaseUrl))
        {
            return Refused("fhirServer is not the trusted issuer's FHIR base URL");
        }

        if (BaseUri(request.FhirServer) is not { } baseUri)
        {
            return Refused(InsecureReason);
        }

        var token = request.Authorization?.AccessToken;
        if (!string.IsNullOrWhiteSpace(token) && token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return Refused("the fhirAuthorization token is not usable");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : request.Timeout);

        var fetches = request.Queries
            .Select(pair => FetchOneAsync(pair.Key, baseUri, pair.Value, token, deadline.Token))
            .ToArray();
        var outcomes = await Task.WhenAll(fetches);
        cancellationToken.ThrowIfCancellationRequested();

        var count = 0;
        var failures = new List<string>();
        foreach (var outcome in outcomes)
        {
            resources[outcome.Key] = outcome.Resource;
            if (outcome.Resource is not null)
            {
                count += CountResources(outcome.Resource);
            }
            else if (outcome.Failure is not null && !failures.Contains(outcome.Failure, StringComparer.Ordinal))
            {
                failures.Add(outcome.Failure);
            }
        }

        return new CdsCallbackResult(
            resources,
            count,
            stopwatch.ElapsedMilliseconds,
            failures.Count == 0 ? null : string.Join("; ", failures));
    }

    public async Task<CdsCallbackProbeResult> ProbeMetadataAsync(string fhirBaseUrl, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        CdsCallbackProbeResult Fail(string reason, int? status = null) =>
            new(false, status, null, null, stopwatch.ElapsedMilliseconds, reason);

        if (BaseUri(fhirBaseUrl) is not { } baseUri)
        {
            return Fail(CdsTrustedIssuer.NormalizeUrl(fhirBaseUrl) is null
                ? "The FHIR base URL is not an absolute http(s) URL."
                : _options.CurrentValue.AllowInsecureCallback
                    ? "The FHIR base URL must not carry a query or fragment."
                    : "The FHIR base URL is not https; callbacks are https only.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "metadata"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJson));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return Fail($"The server answered HTTP {status}.", status);
            }

            var json = await ReadBoundedAsync(response, deadline.Token);
            if (json is null)
            {
                return Fail("The CapabilityStatement is larger than the configured callback response limit.", status);
            }

            if (Parse(json) is not JsonObject capability
                || !string.Equals(Text(capability["resourceType"]), "CapabilityStatement", StringComparison.Ordinal))
            {
                return Fail("The server answered, but not with a FHIR CapabilityStatement.", status);
            }

            var fhirVersion = Text(capability["fhirVersion"]);
            var software = capability["software"] is JsonObject softwareNode
                ? string.Join(' ', new[] { Text(softwareNode["name"]), Text(softwareNode["version"]) }.Where(s => s is not null))
                : null;
            return new CdsCallbackProbeResult(
                true,
                status,
                Bound(fhirVersion, 20),
                Bound(string.IsNullOrWhiteSpace(software) ? null : software, 200),
                stopwatch.ElapsedMilliseconds,
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail($"No answer within {ProbeTimeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException)
        {
            return Fail("The server could not be reached (refused address, DNS, TLS or connection failure).");
        }
    }

    private string InsecureReason =>
        _options.CurrentValue.AllowInsecureCallback ? "the URL is not absolute http(s)" : "the FHIR server is not https";

    /// <summary>The FHIR base with a trailing slash, or null when it is not absolute https (http only when allowed).</summary>
    private Uri? BaseUri(string? fhirBaseUrl)
    {
        if (!Uri.TryCreate(fhirBaseUrl?.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var allowed = uri.Scheme == Uri.UriSchemeHttps
                      || (uri.Scheme == Uri.UriSchemeHttp && _options.CurrentValue.AllowInsecureCallback);
        return allowed ? new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/") : null;
    }

    private async Task<FetchOutcome> FetchOneAsync(
        string key, Uri baseUri, string relativeQuery, string? token, CancellationToken cancellationToken)
    {
        // Relative to the FHIR base only: an absolute, scheme-relative or rooted query could leave the server. The
        // textual checks look at the path part alone: search parameters legitimately carry URLs as token systems
        // (e.g. code=http://loinc.org|4548-4). The resolved target is checked against the base below either way.
        var queryStart = relativeQuery?.IndexOf('?') ?? -1;
        var relativePath = queryStart < 0 ? relativeQuery : relativeQuery![..queryStart];
        if (string.IsNullOrWhiteSpace(relativeQuery)
            || string.IsNullOrWhiteSpace(relativePath)
            || relativePath.StartsWith('/')
            || relativePath.Contains(':')
            || relativePath.Contains('\\')
            || relativePath.Contains("..", StringComparison.Ordinal)
            || !Uri.TryCreate(baseUri, relativeQuery, out var target)
            || !string.Equals(target.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || !target.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
        {
            return new FetchOutcome(key, null, "query not under the FHIR base");
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, target);
            message.Options.Set(FhirBridgeCdsMetrics.UntracedRequest, true);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJson));
            if (!string.IsNullOrWhiteSpace(token))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new FetchOutcome(key, null, $"HTTP {(int)response.StatusCode}");
            }

            var json = await ReadBoundedAsync(response, cancellationToken);
            if (json is null)
            {
                return new FetchOutcome(key, null, "response too large");
            }

            return Parse(json) is JsonObject resource && resource["resourceType"] is JsonValue resourceType
                   && resourceType.TryGetValue<string>(out var type) && !string.IsNullOrEmpty(type)
                   && !string.Equals(type, "OperationOutcome", StringComparison.Ordinal)
                ? new FetchOutcome(key, resource, null)
                : new FetchOutcome(key, null, "not a FHIR resource");
        }
        catch (OperationCanceledException)
        {
            return new FetchOutcome(key, null, "timed out");
        }
        catch (HttpRequestException)
        {
            return new FetchOutcome(key, null, "unreachable");
        }
        catch (FormatException)
        {
            return new FetchOutcome(key, null, "the fhirAuthorization token is not usable");
        }
    }

    /// <summary>The body as text, or null when it is larger than <see cref="CdsOptions.CallbackMaxResponseBytes"/>.</summary>
    private async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var max = Math.Max(1024, _options.CurrentValue.CallbackMaxResponseBytes);
        if (response.Content.Headers.ContentLength > max)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A Bundle counts its entries that carry a resource; anything else counts as one.</summary>
    private static int CountResources(JsonNode resource) =>
        resource is JsonObject obj
        && obj["resourceType"] is JsonValue type && type.TryGetValue<string>(out var name) && name == "Bundle"
            ? (obj["entry"] as JsonArray)?.Count(entry => entry?["resource"] is JsonObject) ?? 0
            : 1;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string? Bound(string? value, int max) =>
        value is null ? null : value.Length > max ? value[..max] : value;

    private sealed record FetchOutcome(string Key, JsonNode? Resource, string? Failure);
}
