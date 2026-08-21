using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ---------------------------------------------------------------------------------------------------------------
// Medplum integration POC. Two modes:
//
//   (default)   validates the WRITE path end-to-end:
//                 1. OAuth2 client_credentials  -> bearer token
//                 2. Conditional upsert          -> PUT {base}/Patient?identifier={system}|{value}   (idempotent)
//                 3. Read-back                   -> GET {base}/Patient?identifier={system}|{value}
//
//   --verify    read-only confirmation of what the write produced (no mutation):
//                 1. token
//                 2. GET {base}/Patient?identifier={system}|{value}   -> resolves the resource + its server id
//                 3. GET {base}/Patient/{id}/_history                 -> version count (idempotency proof)
//               and prints the full resource JSON + one line per version.
//
// Configuration comes from env vars (never hardcode secrets):
//   MEDPLUM_BASE_URL       e.g. https://api.medplum.com/fhir/R4   (default)
//   MEDPLUM_CLIENT_ID      the ClientApplication id
//   MEDPLUM_CLIENT_SECRET  the ClientApplication secret
//   MEDPLUM_TOKEN_URL      optional; derived from base url when omitted
// ---------------------------------------------------------------------------------------------------------------

var verify = args.Contains("--verify", StringComparer.OrdinalIgnoreCase);
var provenance = args.Contains("--provenance", StringComparer.OrdinalIgnoreCase);

var baseUrl = (Environment.GetEnvironmentVariable("MEDPLUM_BASE_URL") ?? "https://api.medplum.com/fhir/R4").TrimEnd('/');
var clientId = Environment.GetEnvironmentVariable("MEDPLUM_CLIENT_ID");
var clientSecret = Environment.GetEnvironmentVariable("MEDPLUM_CLIENT_SECRET");
var tokenUrl = Environment.GetEnvironmentVariable("MEDPLUM_TOKEN_URL") ?? DeriveTokenUrl(baseUrl);

if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
{
    Console.Error.WriteLine("Set MEDPLUM_CLIENT_ID and MEDPLUM_CLIENT_SECRET (see README.md).");
    return 1;
}

// A stable business identifier so re-runs upsert the same Patient rather than creating duplicates.
const string identifierSystem = "https://fhirbridge.poc/patientId";
const string identifierValue = "POC-0001";
var searchUrl = $"{baseUrl}/Patient?identifier={Uri.EscapeDataString(identifierSystem)}%7C{Uri.EscapeDataString(identifierValue)}";

using var http = new HttpClient();

try
{
    Console.WriteLine($"[auth] Requesting client_credentials token from {tokenUrl} ...");
    var token = await GetTokenAsync(http, tokenUrl, clientId!, clientSecret!);
    Console.WriteLine("      OK — got bearer token.");

    if (provenance)
    {
        return await RunProvenanceAsync(http, token, baseUrl);
    }

    return verify
        ? await RunVerifyAsync(http, token, baseUrl, searchUrl)
        : await RunUpsertAsync(http, token, baseUrl, searchUrl);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.Message}");
    return 1;
}

async Task<int> RunUpsertAsync(HttpClient httpClient, string token, string fhirBase, string identifierSearchUrl)
{
    Console.WriteLine("[1/2] Upserting Patient by identifier (conditional PUT) ...");
    var patient = new JsonObject
    {
        ["resourceType"] = "Patient",
        ["identifier"] = new JsonArray(new JsonObject { ["system"] = identifierSystem, ["value"] = identifierValue }),
        ["name"] = new JsonArray(new JsonObject
        {
            ["use"] = "official",
            ["family"] = "PocPatient",
            ["given"] = new JsonArray("Medplum")
        }),
        ["gender"] = "unknown"
    };

    var writtenId = await UpsertAsync(httpClient, token, identifierSearchUrl, patient.ToJsonString());
    Console.WriteLine($"      OK — Medplum resource id: {writtenId}");

    Console.WriteLine("[2/2] Reading it back by identifier ...");
    var (count, _) = await SearchAsync(httpClient, token, identifierSearchUrl);
    Console.WriteLine($"      OK — search matched {count} resource(s) for {identifierSystem}|{identifierValue}.");

    Console.WriteLine();
    Console.WriteLine("SUCCESS. Run again — the id above should stay the same (idempotent upsert, no duplicate).");
    Console.WriteLine("Run with --verify to see the full resource JSON and its version history.");
    return 0;
}

async Task<int> RunVerifyAsync(HttpClient httpClient, string token, string fhirBase, string identifierSearchUrl)
{
    Console.WriteLine($"[1/2] Searching Patient by identifier {identifierSystem}|{identifierValue} ...");
    var (count, resource) = await SearchAsync(httpClient, token, identifierSearchUrl);
    Console.WriteLine($"      Matched {count} resource(s).");

    if (count == 0 || resource is null)
    {
        Console.Error.WriteLine("No matching Patient found — run the POC (without --verify) first to create it.");
        return 1;
    }

    if (count > 1)
    {
        Console.Error.WriteLine($"WARNING: expected exactly 1 resource, found {count} — the upsert may not be idempotent.");
    }

    var resourceId = resource["id"]?.GetValue<string>() ?? "(unknown)";
    var versionId = resource["meta"]?["versionId"]?.GetValue<string>() ?? "(none)";
    Console.WriteLine($"      Resource id: {resourceId}   current versionId: {versionId}");
    Console.WriteLine();
    Console.WriteLine("----- Patient resource JSON -----");
    Console.WriteLine(resource.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("---------------------------------");
    Console.WriteLine();

    Console.WriteLine($"[2/2] Fetching version history: GET {fhirBase}/Patient/{resourceId}/_history ...");
    var versions = await HistoryAsync(httpClient, token, $"{fhirBase}/Patient/{Uri.EscapeDataString(resourceId)}/_history");
    Console.WriteLine($"      {versions.Count} version(s):");
    foreach (var (v, lastUpdated) in versions)
    {
        Console.WriteLine($"        - versionId={v}  lastUpdated={lastUpdated}");
    }

    Console.WriteLine();
    Console.WriteLine(count == 1
        ? $"CONFIRMED. Exactly one Patient; {versions.Count} version(s) under a single id — writes update in place (idempotent)."
        : "Multiple resources matched — investigate the upsert key.");
    return count == 1 ? 0 : 1;
}

// Proves the bug-fix's one external assumption: Provenance has NO `identifier` search parameter in FHIR R4, so it
// can't be conditional-upserted like other resources (that path is what returned 400 "Unknown search parameter:
// identifier"). The fix upserts it by a DETERMINISTIC v5 logical id derived from the source key, via PUT
// Provenance/{uuid}. This mode replays exactly that: PUT the same uuid twice, read it back, and check history shows
// one id with versions accruing (idempotent — no duplicate), mirroring MappedMedplumDestinationWriter.BuildUpsert.
async Task<int> RunProvenanceAsync(HttpClient httpClient, string token, string fhirBase)
{
    // Mirror the writer's key: {system}|{type}|{sourceId} hashed to a v5 UUID under the same fixed namespace.
    var deterministicNamespace = new Guid("8b2f0b3e-3f4a-4c1d-9a7e-2c6d5f9b0a11");
    const string sourceId = "etRVGHu3lF1bbCgzbQAH8CsWG3H0k8nDjHclm3aRDgvY3"; // a real Epic-style Provenance id
    var logicalId = CreateNameBasedUuid(deterministicNamespace, $"urn:fhirbridge:source-id|Provenance|{sourceId}").ToString();
    Console.WriteLine($"[key ] source id '{sourceId}' -> deterministic logical id {logicalId}");

    var url = $"{fhirBase}/Provenance/{logicalId}";
    var body = new JsonObject
    {
        ["resourceType"] = "Provenance",
        ["id"] = logicalId,
        ["target"] = new JsonArray(new JsonObject { ["display"] = "FHIRBridge POC target" }),
        ["recorded"] = "2024-01-01T00:00:00Z",
        ["agent"] = new JsonArray(new JsonObject
        {
            ["who"] = new JsonObject { ["display"] = "FHIRBridge POC" }
        })
    }.ToJsonString();

    Console.WriteLine("[1/3] PUT Provenance/{id} (client-supplied UUID, update-as-create) ...");
    var firstId = await PutByLogicalIdAsync(httpClient, token, url, body);
    Console.WriteLine($"      OK — server accepted the client-supplied id: {firstId}");

    Console.WriteLine("[2/3] PUT the SAME id again (re-run) ...");
    var secondId = await PutByLogicalIdAsync(httpClient, token, url, body);
    Console.WriteLine($"      OK — id after re-run: {secondId}");

    Console.WriteLine($"[3/3] GET Provenance/{logicalId}/_history ...");
    var versions = await HistoryAsync(httpClient, token, $"{fhirBase}/Provenance/{Uri.EscapeDataString(logicalId)}/_history");
    foreach (var (v, lastUpdated) in versions)
    {
        Console.WriteLine($"        - versionId={v}  lastUpdated={lastUpdated}");
    }

    Console.WriteLine();
    var idempotent = string.Equals(firstId, secondId, StringComparison.Ordinal) && firstId == logicalId;
    Console.WriteLine(idempotent
        ? $"SUCCESS. Medplum accepts PUT Provenance/{{uuid}}; both runs resolve to the SAME id ({firstId}) — idempotent, no duplicate. The fix works against live Medplum."
        : "UNEXPECTED — the two runs did not resolve to the same logical id; investigate.");
    return idempotent ? 0 : 1;
}

// PUT to {type}/{id} (logical-id upsert). Returns the server's resource id from the response body.
static async Task<string> PutByLogicalIdAsync(HttpClient http, string token, string url, string fhirJson)
{
    using var request = new HttpRequestMessage(HttpMethod.Put, url)
    {
        Content = new StringContent(fhirJson, Encoding.UTF8, "application/fhir+json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"PUT {url} returned {(int)response.StatusCode}: {Truncate(body)}");
    }

    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "(none)" : "(none)";
}

// RFC 4122 §4.3 name-based (v5, SHA-1) UUID — identical to MappedMedplumDestinationWriter.CreateNameBasedUuid.
static Guid CreateNameBasedUuid(Guid namespaceId, string name)
{
    var namespaceBytes = namespaceId.ToByteArray();
    SwapGuidByteOrder(namespaceBytes);

    var nameBytes = Encoding.UTF8.GetBytes(name);
    var toHash = new byte[namespaceBytes.Length + nameBytes.Length];
    Buffer.BlockCopy(namespaceBytes, 0, toHash, 0, namespaceBytes.Length);
    Buffer.BlockCopy(nameBytes, 0, toHash, namespaceBytes.Length, nameBytes.Length);

    var hash = System.Security.Cryptography.SHA1.HashData(toHash);

    var uuid = new byte[16];
    Array.Copy(hash, 0, uuid, 0, 16);
    uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
    uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);

    SwapGuidByteOrder(uuid);
    return new Guid(uuid);
}

static void SwapGuidByteOrder(byte[] guid)
{
    (guid[0], guid[3]) = (guid[3], guid[0]);
    (guid[1], guid[2]) = (guid[2], guid[1]);
    (guid[4], guid[5]) = (guid[5], guid[4]);
    (guid[6], guid[7]) = (guid[7], guid[6]);
}

static string DeriveTokenUrl(string fhirBaseUrl)
{
    const string suffix = "/fhir/R4";
    var root = fhirBaseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        ? fhirBaseUrl[..^suffix.Length]
        : fhirBaseUrl;
    return $"{root.TrimEnd('/')}/oauth2/token";
}

static async Task<string> GetTokenAsync(HttpClient http, string tokenUrl, string clientId, string clientSecret)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
    {
        Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
        })
    };
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"token endpoint returned {(int)response.StatusCode}: {Truncate(body)}");
    }

    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.GetProperty("access_token").GetString()
           ?? throw new InvalidOperationException("token response had no access_token");
}

static async Task<string> UpsertAsync(HttpClient http, string token, string url, string fhirJson)
{
    using var request = new HttpRequestMessage(HttpMethod.Put, url)
    {
        Content = new StringContent(fhirJson, Encoding.UTF8, "application/fhir+json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"upsert returned {(int)response.StatusCode}: {Truncate(body)}");
    }

    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "(none)" : "(none)";
}

// Returns (matchCount, firstResource) for an identifier search. firstResource is the first entry's resource node.
static async Task<(int Count, JsonObject? Resource)> SearchAsync(HttpClient http, string token, string searchUrl)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"search returned {(int)response.StatusCode}: {Truncate(body)}");
    }

    var bundle = JsonNode.Parse(body) as JsonObject;
    var entries = bundle?["entry"] as JsonArray;
    var count = bundle?["total"]?.GetValue<int>() ?? entries?.Count ?? 0;
    var first = entries is { Count: > 0 } && entries[0] is JsonObject e ? e["resource"] as JsonObject : null;
    return (count, first);
}

// Returns each version's (versionId, lastUpdated) from an instance _history bundle, newest first.
static async Task<List<(string VersionId, string LastUpdated)>> HistoryAsync(HttpClient http, string token, string historyUrl)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, historyUrl);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"_history returned {(int)response.StatusCode}: {Truncate(body)}");
    }

    var result = new List<(string, string)>();
    if (JsonNode.Parse(body) is JsonObject bundle && bundle["entry"] is JsonArray entries)
    {
        foreach (var entry in entries)
        {
            var meta = (entry as JsonObject)?["resource"]?["meta"];
            result.Add((
                meta?["versionId"]?.GetValue<string>() ?? "(none)",
                meta?["lastUpdated"]?.GetValue<string>() ?? "(none)"));
        }
    }

    return result;
}

static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";
