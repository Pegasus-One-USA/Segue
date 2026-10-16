using System.Collections.Concurrent;
using System.Net;

namespace FHIRBridge.Api.IntegrationTests.TestHelpers;

/// <summary>
/// The outside world the CDS Hooks clients talk to, kept in-process so the suite stays hermetic: the primary handler
/// of the JWKS client and of the FHIR callback client (see <see cref="ApiFactory"/>). Tests map URL prefixes on
/// their own unique hosts (an EHR's JWKS URL, its FHIR base) to responses; anything unmapped is 404. Every request
/// is recorded with its Authorization header so a test can check what was sent.
/// </summary>
public static class CdsTestHttpEndpoints
{
    private static readonly ConcurrentDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<SeenRequest> SeenRequests = new();

    /// <summary>Answers every request whose absolute URL starts with <paramref name="urlPrefix"/> (longest prefix wins).</summary>
    public static void Map(string urlPrefix, Func<HttpRequestMessage, HttpResponseMessage> respond) => Routes[urlPrefix] = respond;

    /// <summary>The requests sent to URLs starting with <paramref name="urlPrefix"/>.</summary>
    public static IReadOnlyList<SeenRequest> SeenUnder(string urlPrefix) =>
        SeenRequests.Where(seen => seen.Url.StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/fhir+json") };

    /// <param name="Body">The request body (POSTs, e.g. a relayed CRD call); null when there is none.</param>
    public sealed record SeenRequest(string Url, string? Authorization, string? Body = null);

    /// <summary>The primary handler installed on the CDS clients in the test host.</summary>
    public sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            SeenRequests.Enqueue(new SeenRequest(url, request.Headers.Authorization?.ToString(), body));
            var route = Routes
                .Where(pair => url.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pair => pair.Key.Length)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            return Task.FromResult(route?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
