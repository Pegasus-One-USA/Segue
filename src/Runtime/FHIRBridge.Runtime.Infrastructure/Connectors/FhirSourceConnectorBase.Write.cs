using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// The write half of the vendor connector: creates, identifier search and <c>Patient/$match</c> for EHR write-back.
///
/// <para><b>Why it has its own send loop.</b> <c>SendWithRetryAsync</c> retries every timeout, dropped connection and
/// 5xx, which is right for a GET and wrong for a create: the first attempt may have landed, and an EHR files a
/// replayed allergy, problem or note a second time. Here a create is sent once, and retried only on 429 and on a 503
/// that carries Retry-After, which a server sends instead of doing the work. Every other open outcome comes back as
/// <see cref="FhirWriteOutcomeKind.OutcomeUnknown"/> for the caller to record and leave for a person. The HttpClient's
/// own resilience handler is configured not to retry unsafe methods for the same reason (see the Runtime DI).</para>
///
/// <para><b>PHI.</b> Request bodies, response bodies, identifier values and OperationOutcome diagnostics are never
/// logged. Search URLs are logged with their query redacted.</para>
/// </summary>
public abstract partial class FhirSourceConnectorBase : IFhirWriteClient
{
    private const string FhirJsonMediaType = "application/fhir+json";

    public async Task<FhirWriteResult> CreateAsync(
        string resourceType,
        string resourceJson,
        bool returnRepresentation,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing was sent, so this is a refusal, not an open outcome: safe to retry once the cause is fixed.
            _logger.LogWarning("{Source} create of {ResourceType} not sent: no access token ({ExceptionType}).", SourceDisplayName, resourceType, ex.GetType().Name);
            return new FhirWriteResult(FhirWriteOutcomeKind.Rejected, null, null, null, null, [TokenIssue()]);
        }

        var requestUrl = WithAdditionalQuery($"{source.BaseUrl.TrimEnd('/')}/{resourceType}", source);
        var exchange = await SendOnceOrOnRefusalAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = new StringContent(resourceJson, Encoding.UTF8, FhirJsonMediaType),
                };
                request.Headers.TryAddWithoutValidation("Prefer", returnRepresentation ? "return=representation" : "return=minimal");
                return Authorize(request, accessToken);
            },
            idempotent: false,
            source,
            cancellationToken);

        if (exchange.Response is not { } response)
        {
            if (exchange.RefusedBeforeSend)
            {
                // Nothing left this process (open circuit, rate limiter): a refusal the next run may retry.
                _logger.LogWarning("{Source} create of {ResourceType} not sent ({Reason}).", SourceDisplayName, resourceType, exchange.FailureReason);
                return new FhirWriteResult(FhirWriteOutcomeKind.Rejected, null, null, null, null, [NotSentIssue()]);
            }

            _logger.LogWarning("{Source} create of {ResourceType}: outcome unknown ({Reason}).", SourceDisplayName, resourceType, exchange.FailureReason);
            return new FhirWriteResult(FhirWriteOutcomeKind.OutcomeUnknown, null, null, null, null, []);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await ReadBodyAsync(response, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var (id, version) = ParseLocation(response.Headers.Location, resourceType);
                id ??= ReadId(body);
                _logger.LogInformation("{Source} created {ResourceType} ({StatusCode}).", SourceDisplayName, resourceType, status);
                return new FhirWriteResult(
                    id is null ? FhirWriteOutcomeKind.OutcomeUnknown : FhirWriteOutcomeKind.Created,
                    status, id, version, string.IsNullOrWhiteSpace(body) ? null : body, ParseIssues(body));
            }

            var issues = ParseIssues(body);
            if (issues.Count == 0 && (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                issues = [new FhirOperationOutcomeIssue("error", "security", DescribeAuthenticationChallenge(response) is { } challenge ? [challenge] : [], [])];
            }

            // 429 (and a 503 that kept carrying Retry-After until retries ran out) is a refusal made before any work,
            // so it is a rejection the next run may retry. Any other 5xx may have filed the record.
            var refusedBeforeWork = status == 429
                || (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null);
            var kind = !refusedBeforeWork && status >= 500 ? FhirWriteOutcomeKind.OutcomeUnknown : FhirWriteOutcomeKind.Rejected;
            _logger.LogWarning(
                "{Source} create of {ResourceType} {Outcome} ({StatusCode}); outcome codes [{OutcomeCodes}].",
                SourceDisplayName, resourceType, kind, status, string.Join(",", issues.SelectMany(i => i.DetailCodes.DefaultIfEmpty(i.Code ?? "-"))));
            return new FhirWriteResult(kind, status, null, null, null, issues);
        }
    }

    public async Task<FhirRawWriteResult> SendAsync(
        FhirRawWriteRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("{Source} {Method} not sent: no access token ({ExceptionType}).", SourceDisplayName, request.Method, ex.GetType().Name);
            return new FhirRawWriteResult(FhirWriteOutcomeKind.Rejected, null, null, [TokenIssue()]);
        }

        var requestUrl = request.AddSourceQueryParameters ? WithAdditionalQuery(request.Url, source) : request.Url;
        var method = new HttpMethod(request.Method);
        var exchange = await SendOnceOrOnRefusalAsync(
            () => Authorize(new HttpRequestMessage(method, requestUrl) { Content = BuildContent(request) }, accessToken),
            request.Idempotent,
            source,
            cancellationToken);

        if (exchange.Response is not { } response)
        {
            if (exchange.RefusedBeforeSend)
            {
                _logger.LogWarning("{Source} {Method} {Url} not sent ({Reason}).", SourceDisplayName, request.Method, RedactRequestUrl(requestUrl), exchange.FailureReason);
                return new FhirRawWriteResult(FhirWriteOutcomeKind.Rejected, null, null, [NotSentIssue()]);
            }

            _logger.LogWarning("{Source} {Method} {Url}: outcome unknown ({Reason}).", SourceDisplayName, request.Method, RedactRequestUrl(requestUrl), exchange.FailureReason);
            return new FhirRawWriteResult(FhirWriteOutcomeKind.OutcomeUnknown, null, null, []);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await ReadBodyAsync(response, cancellationToken);
            var issues = ParseIssues(body);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("{Source} {Method} {Url} returned {StatusCode}.", SourceDisplayName, request.Method, RedactRequestUrl(requestUrl), status);
                return new FhirRawWriteResult(FhirWriteOutcomeKind.Created, status, body, issues);
            }

            if (issues.Count == 0 && (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                issues = [new FhirOperationOutcomeIssue("error", "security", DescribeAuthenticationChallenge(response) is { } challenge ? [challenge] : [], [])];
            }

            var refusedBeforeWork = status == 429
                || (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null);
            var kind = !request.Idempotent && !refusedBeforeWork && status >= 500
                ? FhirWriteOutcomeKind.OutcomeUnknown
                : FhirWriteOutcomeKind.Rejected;
            _logger.LogWarning("{Source} {Method} {Url} {Outcome} ({StatusCode}).", SourceDisplayName, request.Method, RedactRequestUrl(requestUrl), kind, status);
            return new FhirRawWriteResult(kind, status, body, issues);
        }
    }

    private static HttpContent? BuildContent(FhirRawWriteRequest request)
    {
        if (request.FormFields is { } fields)
        {
            if (!request.Multipart)
            {
                return new FormUrlEncodedContent(fields);
            }

            var multipart = new MultipartFormDataContent();
            foreach (var (name, value) in fields)
            {
                multipart.Add(new StringContent(value, Encoding.UTF8), name);
            }

            return multipart;
        }

        return request.Body is null
            ? null
            : new StringContent(request.Body, Encoding.UTF8, request.ContentType ?? FhirJsonMediaType);
    }

    public Task<FhirSearchPage> SearchByIdentifierAsync(
        string resourceType,
        string system,
        string value,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken) =>
        SearchFirstPageAsync(resourceType, "identifier=" + Uri.EscapeDataString($"{system}|{value}"), source, cancellationToken);

    public Task<FhirSearchPage> SearchForPatientAsync(
        string resourceType,
        string patientId,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken) =>
        SearchFirstPageAsync(resourceType, "patient=" + Uri.EscapeDataString(patientId), source, cancellationToken);

    public async Task<FhirPatientMatchResult> MatchPatientAsync(
        string patientJson,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("{Source} patient match not sent: no access token ({ExceptionType}).", SourceDisplayName, ex.GetType().Name);
            return new FhirPatientMatchResult(FhirPatientMatchKind.Failed, null, null, [TokenIssue()]);
        }

        string body;
        try
        {
            body = BuildMatchParameters(patientJson);
        }
        catch (JsonException)
        {
            return new FhirPatientMatchResult(FhirPatientMatchKind.Failed, null, null, []);
        }

        var requestUrl = WithAdditionalQuery($"{source.BaseUrl.TrimEnd('/')}/Patient/$match", source);
        var exchange = await SendOnceOrOnRefusalAsync(
            () => Authorize(
                new HttpRequestMessage(HttpMethod.Post, requestUrl)
                {
                    Content = new StringContent(body, Encoding.UTF8, FhirJsonMediaType),
                },
                accessToken),
            idempotent: true,
            source,
            cancellationToken);

        if (exchange.Response is not { } response)
        {
            _logger.LogWarning("{Source} patient match failed ({Reason}).", SourceDisplayName, exchange.FailureReason);
            return new FhirPatientMatchResult(FhirPatientMatchKind.Failed, null, null, []);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var responseBody = await ReadBodyAsync(response, cancellationToken);
            var result = InterpretMatch(status, responseBody);
            _logger.LogInformation(
                "{Source} patient match: {MatchKind} ({StatusCode}); outcome codes [{OutcomeCodes}].",
                SourceDisplayName, result.Kind, status, string.Join(",", result.Issues.SelectMany(i => i.DetailCodes)));
            return result;
        }
    }

    /// <summary>
    /// Certain-only matching (Epic). 200 with exactly one patient entry whose <c>search.score</c> is 1 is a certain
    /// match. 200 with no patient entry (Epic 4101) is none. A 400 that is an OperationOutcome about the candidates
    /// (Epic 59011 several high-confidence matches, 59013 low-confidence only) needs a person. Anything else failed.
    /// </summary>
    internal static FhirPatientMatchResult InterpretMatch(int status, string? body)
    {
        var issues = ParseIssues(body);
        JsonElement root = default;
        var parsed = false;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                root = document.RootElement.Clone();
                parsed = true;
            }
            catch (JsonException)
            {
                parsed = false;
            }
        }

        if (status is >= 200 and < 300 && parsed && root.ValueKind == JsonValueKind.Object)
        {
            var candidates = new List<(string Id, double? Score)>();
            if (root.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("resource", out var resource)
                        || !resource.TryGetProperty("resourceType", out var type)
                        || type.GetString() != "Patient"
                        || !resource.TryGetProperty("id", out var id)
                        || id.GetString() is not { Length: > 0 } patientId)
                    {
                        continue;
                    }

                    double? score = entry.TryGetProperty("search", out var search)
                        && search.TryGetProperty("score", out var scoreElement)
                        && scoreElement.TryGetDouble(out var value)
                            ? value
                            : null;
                    candidates.Add((patientId, score));
                }
            }

            // "No one" must be said explicitly (Epic 4101, with no error alongside it): an empty answer for any other
            // reason (e.g. 59011 several matches) is never read as permission to create a patient. A single
            // candidate counts only with a score of 1.
            var saysNoResults = issues.Any(i => i.DetailCodes.Contains("4101"))
                && !issues.Any(i => i.Severity is "error" or "fatal");
            return candidates.Count switch
            {
                0 when saysNoResults => new FhirPatientMatchResult(FhirPatientMatchKind.None, null, status, issues),
                1 when candidates[0].Score is >= 1.0 => new FhirPatientMatchResult(FhirPatientMatchKind.Certain, candidates[0].Id, status, issues),
                _ => new FhirPatientMatchResult(FhirPatientMatchKind.Ambiguous, null, status, issues),
            };
        }

        // Epic answers "possible but not certain" candidates with 400 + a processing issue; a malformed request is
        // an 'invalid' or 'not-supported' issue instead, which is a configuration problem, not a matching one.
        var aboutCandidates = status == 400
            && issues.Count > 0
            && issues.All(i => i.Code is "processing" or "multiple-matches" or "duplicate");
        return new FhirPatientMatchResult(
            aboutCandidates ? FhirPatientMatchKind.Ambiguous : FhirPatientMatchKind.Failed, null, status, issues);
    }

    private async Task<FhirSearchPage> SearchFirstPageAsync(
        string resourceType,
        string query,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("{Source} {ResourceType} search not sent: no access token ({ExceptionType}).", SourceDisplayName, resourceType, ex.GetType().Name);
            return new FhirSearchPage(false, null, [], [TokenIssue()]);
        }

        var requestUrl = WithAdditionalQuery($"{source.BaseUrl.TrimEnd('/')}/{resourceType}?{query}", source);
        var exchange = await SendOnceOrOnRefusalAsync(
            () => Authorize(new HttpRequestMessage(HttpMethod.Get, requestUrl), accessToken),
            idempotent: true,
            source,
            cancellationToken);

        if (exchange.Response is not { } response)
        {
            _logger.LogWarning("{Source} search {Url} failed ({Reason}).", SourceDisplayName, RedactRequestUrl(requestUrl), exchange.FailureReason);
            return new FhirSearchPage(false, null, [], []);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await ReadBodyAsync(response, cancellationToken);
            var issues = ParseIssues(body);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Source} search {Url} returned {StatusCode}.", SourceDisplayName, RedactRequestUrl(requestUrl), status);
                return new FhirSearchPage(false, status, [], issues);
            }

            var resources = new List<string>();
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in entries.EnumerateArray())
                        {
                            if (entry.TryGetProperty("resource", out var resource)
                                && resource.TryGetProperty("resourceType", out var type)
                                && type.GetString() != "OperationOutcome")
                            {
                                resources.Add(resource.GetRawText());
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    return new FhirSearchPage(false, status, [], issues);
                }
            }

            _logger.LogInformation("{Source} search {Url} returned {Count} resources.", SourceDisplayName, RedactRequestUrl(requestUrl), resources.Count);
            return new FhirSearchPage(true, status, resources, issues);
        }
    }

    /// <summary>
    /// Sends a request built fresh for each attempt. Idempotent requests are retried like reads. A non-idempotent one
    /// is retried only on a status that proves the server refused it before doing any work (429, and 503 with
    /// Retry-After); a timeout, a dropped connection or caller cancellation after it may have been sent ends the
    /// exchange with no response, which the caller must treat as an unknown outcome.
    /// </summary>
    private async Task<WriteExchange> SendOnceOrOnRefusalAsync(
        Func<HttpRequestMessage> buildRequest,
        bool idempotent,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        var maxRetryCount = Math.Max(0, _options.MaxRetryCount);
        var requestTimeoutSeconds = source.TimeoutSeconds is { } configured && configured > 0
            ? configured
            : _options.RequestTimeoutSeconds;

        for (var attempt = 0; ; attempt++)
        {
            await WaitForSourceThrottleAsync(source, cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (requestTimeoutSeconds > 0)
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
            }

            using var request = buildRequest();
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (idempotent)
                {
                    throw;
                }

                // The caller gave up, but the request may already be on the server.
                return new WriteExchange(null, "cancelled");
            }
            catch (Exception ex) when (ex.GetType().Name is "BrokenCircuitException" or "IsolatedCircuitException" or "RateLimiterRejectedException")
            {
                // The resilience pipeline refused before handing the request to the network. Named rather than typed:
                // the Polly types are an implementation detail of the host's resilience handler.
                return new WriteExchange(null, "refused-before-send", RefusedBeforeSend: true);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or TimeoutException
                || ex.GetType().Name == "TimeoutRejectedException")
            {
                if (idempotent && attempt < maxRetryCount)
                {
                    await Task.Delay(GetRetryDelay(null, attempt), cancellationToken);
                    continue;
                }

                return new WriteExchange(null, ex is HttpRequestException ? "connection-failed" : "timed-out");
            }

            var retryable = idempotent
                ? IsTransient(response)
                : response.StatusCode == (HttpStatusCode)429
                    || (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null);
            if (!retryable || attempt >= maxRetryCount)
            {
                return new WriteExchange(response, null);
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "{Source} {Method} returned {StatusCode}. Retrying attempt {Attempt}/{MaxRetryCount} after {DelayMs} ms.",
                SourceDisplayName, request.Method, (int)response.StatusCode, attempt + 1, maxRetryCount, delay.TotalMilliseconds);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private HttpRequestMessage Authorize(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        ConfigureRequestHeaders(request);
        return request;
    }

    private string WithAdditionalQuery(string url, FhirSourceConfiguration source)
    {
        var questionMark = url.IndexOf('?', StringComparison.Ordinal);
        var path = questionMark < 0 ? url : url[..questionMark];
        var query = MergeAdditionalQueryParameters(questionMark < 0 ? null : url[(questionMark + 1)..], source);
        return string.IsNullOrWhiteSpace(query) ? path : $"{path}?{query}";
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>The id (and version) from a Location header, relative (<c>Condition/abc</c>, which is what Epic sends)
    /// or absolute (<c>https://host/R4/Condition/abc/_history/2</c>). Uses the original string: a relative Uri has no
    /// AbsolutePath and throws if asked for one.</summary>
    internal static (string? Id, string? Version) ParseLocation(Uri? location, string resourceType)
    {
        if (location is null)
        {
            return (null, null);
        }

        var text = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        var query = text.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            text = text[..query];
        }

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            if (!string.Equals(segments[i], resourceType, StringComparison.Ordinal))
            {
                continue;
            }

            var id = Uri.UnescapeDataString(segments[i + 1]);
            var version = i + 3 < segments.Length && segments[i + 2] == "_history" ? segments[i + 3] : null;
            return (id, version);
        }

        return (null, null);
    }

    private static string? ReadId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>OperationOutcome issues from a bare OperationOutcome or from OperationOutcome entries of a Bundle.
    /// Codes and element paths only.</summary>
    internal static IReadOnlyList<FhirOperationOutcomeIssue> ParseIssues(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var issues = new List<FhirOperationOutcomeIssue>();
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            if (StringOf(root, "resourceType") == "OperationOutcome")
            {
                AddIssues(root, issues);
            }
            else if (root.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.TryGetProperty("resource", out var resource) && StringOf(resource, "resourceType") == "OperationOutcome")
                    {
                        AddIssues(resource, issues);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return issues;
    }

    private static void AddIssues(JsonElement outcome, List<FhirOperationOutcomeIssue> issues)
    {
        if (!outcome.TryGetProperty("issue", out var issueArray) || issueArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var issue in issueArray.EnumerateArray())
        {
            var detailCodes = new List<string>();
            if (issue.TryGetProperty("details", out var details)
                && details.TryGetProperty("coding", out var codings)
                && codings.ValueKind == JsonValueKind.Array)
            {
                detailCodes.AddRange(codings.EnumerateArray().Select(c => StringOf(c, "code")).OfType<string>());
            }

            var expressions = new List<string>();
            foreach (var property in new[] { "expression", "location" })
            {
                if (issue.TryGetProperty(property, out var paths) && paths.ValueKind == JsonValueKind.Array)
                {
                    expressions.AddRange(paths.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!));
                }
            }

            issues.Add(new FhirOperationOutcomeIssue(StringOf(issue, "severity"), StringOf(issue, "code"), detailCodes, expressions));
        }
    }

    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Wraps a Patient resource in the <c>$match</c> Parameters body. <c>onlyCertainMatches</c> must be the
    /// boolean true: Epic refuses the string "true" its own sample sends (59102) and does not support false (59138).</summary>
    private static string BuildMatchParameters(string patientJson)
    {
        using var patient = JsonDocument.Parse(patientJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("resourceType", "Parameters");
            writer.WriteStartArray("parameter");
            writer.WriteStartObject();
            writer.WriteString("name", "resource");
            writer.WritePropertyName("resource");
            patient.RootElement.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("name", "onlyCertainMatches");
            writer.WriteBoolean("valueBoolean", true);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static FhirOperationOutcomeIssue TokenIssue() => new("error", "security", ["token-unavailable"], []);

    private static FhirOperationOutcomeIssue NotSentIssue() => new("error", "transient", ["not-sent"], []);

    private sealed record WriteExchange(HttpResponseMessage? Response, string? FailureReason, bool RefusedBeforeSend = false);
}
