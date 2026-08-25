using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace FHIRBridge.Tools.TerminologyServerPoc;

/// <summary>
/// Minimal client for the embedded HAPI FHIR terminology server. New, standalone class —
/// does not reuse or modify FHIRBridge.Infrastructure's Fhir*TerminologyService classes,
/// though it talks to the same standard FHIR terminology operations they do
/// ($lookup / $validate-code) so the shape is directly comparable.
/// </summary>
internal sealed class HapiTerminologyClient
{
    private readonly HttpClient _http;

    public HapiTerminologyClient(string baseUrl, TimeSpan? timeout = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(100),
        };
    }

    /// <summary>Deletes a CodeSystem by id, if present. Used to retire a superseded resource
    /// (e.g. the small demo subset) before loading its full-scale replacement under the same
    /// canonical system URL, avoiding two CodeSystem resources claiming the same url/version.</summary>
    public async Task DeleteCodeSystemAsync(string resourceId, CancellationToken ct)
    {
        var response = await _http.DeleteAsync($"CodeSystem/{resourceId}", ct);
        // 404 is fine — nothing to retire. Anything else unexpected we surface.
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"  ~ Delete of {resourceId} returned {(int)response.StatusCode}: {body}");
        }
    }

    public async Task<bool> UploadCodeSystemAsync(object codeSystemResource, string resourceId, CancellationToken ct)
    {
        var response = await _http.PutAsJsonAsync($"CodeSystem/{resourceId}", codeSystemResource, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"  ✗ Upload failed: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}");
            return false;
        }

        return true;
    }

    public async Task<(bool Found, string? Display)> LookupAsync(string system, string code, CancellationToken ct)
    {
        var query = $"CodeSystem/$lookup?system={HttpUtility.UrlEncode(system)}&code={HttpUtility.UrlEncode(code)}";
        var response = await _http.GetAsync(query, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("parameter", out var parameters))
        {
            return (true, null);
        }

        foreach (var p in parameters.EnumerateArray())
        {
            if (p.TryGetProperty("name", out var name) && name.GetString() == "display" &&
                p.TryGetProperty("valueString", out var value))
            {
                return (true, value.GetString());
            }
        }

        return (true, null);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync("metadata", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
