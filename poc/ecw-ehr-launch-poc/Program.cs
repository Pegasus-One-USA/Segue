using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

// ---------------------------------------------------------------------------
// eClinicalWorks — Provider EMR Launch POC
//
// A single-file, standalone proof-of-concept that validates the full SMART on
// FHIR *EHR launch* round-trip against the eCW sandbox BEFORE any FHIRBridge
// development:
//   1. eCW EMR opens the registered Launch URL with ?iss=&launch=
//   2. we start authorization_code + PKCE and redirect to eCW's authorize
//   3. eCW redirects back to the Redirect URL with ?code=&state=
//   4. we exchange the code (confidential / symmetric client_secret) for a token
//   5. we call the FHIR API with the token and render the launched patient's data
//
// It reuses the SAME paths registered on the eCW dev portal
// (https://localhost:5000/api/v1/oauth/launch + /api/v1/oauth/callback) so the
// portal's "Launch" button works against this POC with no re-registration.
// NOT production code: state is in-memory, secrets come from user-secrets.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
// Load user-secrets regardless of environment (dotnet run defaults to Production, where
// user-secrets would otherwise NOT be auto-loaded — leaving the appsettings placeholders in play).
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);
var app = builder.Build();

var cfg = app.Configuration.GetSection("Ecw");
string clientId = Require(cfg["ClientId"], "Ecw:ClientId");
string clientSecret = Require(cfg["ClientSecret"], "Ecw:ClientSecret");
string authorizeEndpoint = Require(cfg["AuthorizeEndpoint"], "Ecw:AuthorizeEndpoint");
string tokenEndpoint = Require(cfg["TokenEndpoint"], "Ecw:TokenEndpoint");
string redirectUri = cfg["RedirectUri"] ?? "https://localhost:5000/api/v1/oauth/callback";
string scope = cfg["Scope"] ?? "launch offline_access user/Patient.read";
// How the confidential client authenticates at the token endpoint: "basic" (client_secret_basic —
// creds in the Authorization header) or "post" (client_secret_post — creds in the form body).
// eCW returned invalid_client for client_secret_post, so default to basic.
string authPlacement = cfg["AuthPlacement"] ?? "basic";

var http = new HttpClient();

// state -> (PKCE code_verifier, iss/FHIR base this launch resolved to). In-memory: POC only.
var pending = new ConcurrentDictionary<string, (string Verifier, string Iss)>();

// --- 1) LAUNCH: eCW hits this with ?iss=&launch= (registered Launch URL) ------
app.MapGet("/api/v1/oauth/launch", (string? iss, string? launch) => StartLaunch(iss, launch));
app.MapGet("/api/v1/oauth/launch/{**rest}", (string? iss, string? launch) => StartLaunch(iss, launch));

IResult StartLaunch(string? iss, string? launch)
{
    Console.WriteLine($"[launch] iss={iss} launch={(string.IsNullOrEmpty(launch) ? "" : "<present>")}");
    if (string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(launch))
        return Html("<h2>Missing <code>iss</code> or <code>launch</code></h2><p>This endpoint must be opened by the eCW EMR launch, not directly.</p>");

    var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
    var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    var state = Guid.NewGuid().ToString("N");
    pending[state] = (verifier, iss);

    var query = new Dictionary<string, string?>
    {
        ["response_type"] = "code",
        ["client_id"] = clientId,
        ["redirect_uri"] = redirectUri,
        ["scope"] = scope,
        ["state"] = state,
        ["aud"] = iss,                       // eCW: aud MUST equal the FHIR base (iss)
        ["launch"] = launch,                 // opaque launch token restores patient/encounter context
        ["code_challenge"] = challenge,
        ["code_challenge_method"] = "S256",
    };
    var authorizeUrl = QueryHelpers.AddQueryString(authorizeEndpoint, query);
    Console.WriteLine($"[launch] redirecting to authorize (state={state})");

    // eCW opens the launch inside an IFRAME in its portal, but the eCW sign-in page cannot be
    // framed (X-Frame-Options / CSP). A plain 302 would try to load the login inside that iframe
    // and fail. So instead return a tiny page that navigates the TOP window out to the authorize
    // URL (with a user-clickable target=_top fallback if scripted top-navigation is sandboxed off).
    var jsUrl = JsonSerializer.Serialize(authorizeUrl);
    var safeUrl = WebUtility.HtmlEncode(authorizeUrl);
    return Html($@"
        <h3>Redirecting to eClinicalWorks sign-in…</h3>
        <p>If you are viewing this inside the portal and nothing happens,
           <a href='{safeUrl}' target='_top' style='font-size:1.15rem;font-weight:600'>click here to continue &raquo;</a></p>
        <script>
          var u = {jsUrl};
          try {{
            if (window.top && window.top !== window.self) {{ window.top.location.href = u; }}
            else {{ window.location.href = u; }}
          }} catch (e) {{ window.location.href = u; }}
        </script>");
}

// --- 2) CALLBACK: eCW redirects back with ?code=&state= (registered Redirect URL)
app.MapGet("/api/v1/oauth/callback", async (string? code, string? state, string? error, string? error_description) =>
{
    Console.WriteLine($"[callback] hasCode={!string.IsNullOrEmpty(code)} state={state} error={error}");
    if (!string.IsNullOrWhiteSpace(error))
        return Html($"<h2>❌ Authorization error</h2><p><b>{WebUtility.HtmlEncode(error)}</b>: {WebUtility.HtmlEncode(error_description)}</p>");
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || !pending.TryRemove(state, out var s))
        return Html("<h2>❌ Invalid callback</h2><p>Missing code/state, or state not found (server restarted?).</p>");

    // --- 3) TOKEN EXCHANGE (confidential client) ------------------------------
    var form = new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = redirectUri,
        ["code_verifier"] = s.Verifier,
    };
    using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
    if (string.Equals(authPlacement, "basic", StringComparison.OrdinalIgnoreCase))
    {
        // client_secret_basic: client_id + client_secret in the Authorization header (NOT the body).
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }
    else
    {
        // client_secret_post: creds in the form body.
        form["client_id"] = clientId;
        form["client_secret"] = clientSecret;
    }
    tokenRequest.Content = new FormUrlEncodedContent(form);
    Console.WriteLine($"[callback] token exchange via client_secret_{authPlacement}");
    var tokenResponse = await http.SendAsync(tokenRequest);
    var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
    if (!tokenResponse.IsSuccessStatusCode)
        return Html($"<h2>❌ Token exchange failed — HTTP {(int)tokenResponse.StatusCode}</h2><pre>{WebUtility.HtmlEncode(tokenBody)}</pre>" +
                    "<p>Common causes: wrong client_secret, redirect_uri mismatch, or (outside the US) eCW blocking the call — a US VPN is required.</p>");

    using var doc = JsonDocument.Parse(tokenBody);
    var root = doc.RootElement;
    string accessToken = root.GetProperty("access_token").GetString()!;
    string? patient = root.TryGetProperty("patient", out var p) ? p.GetString() : null;
    string? grantedScope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
    bool hasRefresh = root.TryGetProperty("refresh_token", out _);
    int expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 0;
    Console.WriteLine($"[callback] token ok. patientCtx={!string.IsNullOrEmpty(patient)} refresh={hasRefresh} expiresIn={expiresIn}");

    // --- 4) FHIR READS using the access token (multi-resource) ----------------
    string fhirSection;
    if (string.IsNullOrWhiteSpace(patient))
    {
        fhirSection = "<p>(no <code>patient</code> in the token response — launch carried no patient context)</p>";
    }
    else
    {
        var fhirBase = s.Iss.TrimEnd('/');
        // Patient is a read-by-id; the rest are patient-scoped searches (return a Bundle).
        var queries = new (string Label, string Url)[]
        {
            ("Patient",                  $"{fhirBase}/Patient/{patient}"),
            ("Condition",                $"{fhirBase}/Condition?patient={patient}"),
            // eCW rejects the `_count` param ("Unsupported query parameter(s): _count"). Category is a
            // valid US Core Observation filter and works; just never send _count to eCW.
            ("Observation (vital-signs)", $"{fhirBase}/Observation?patient={patient}&category=vital-signs"),
            ("Observation (laboratory)",  $"{fhirBase}/Observation?patient={patient}&category=laboratory"),
            ("MedicationRequest",        $"{fhirBase}/MedicationRequest?patient={patient}"),
            ("AllergyIntolerance",       $"{fhirBase}/AllergyIntolerance?patient={patient}"),
        };

        var rows = new StringBuilder();
        var details = new StringBuilder();
        foreach (var (label, url) in queries)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            req.Headers.TryAddWithoutValidation("Accept", "application/fhir+json");
            int status;
            string body;
            try
            {
                var resp = await http.SendAsync(req);
                status = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex) { status = 0; body = ex.Message; }

            var summary = Summarize(body);
            Console.WriteLine($"[callback] FHIR {label} -> {status} ({summary})");
            if (status != 200)
            {
                var snippet = (body.Length > 500 ? body[..500] : body).Replace("\r", " ").Replace("\n", " ");
                Console.WriteLine($"[callback]   ^ error body: {snippet}");
            }
            rows.Append($"<tr><td><b>{label}</b></td><td>{(status == 200 ? "✅" : "⚠️")} {status}</td><td>{WebUtility.HtmlEncode(summary)}</td></tr>");
            details.Append($"<details><summary>{label} — {WebUtility.HtmlEncode(url)}</summary>" +
                           $"<pre style='max-height:360px;overflow:auto;background:#f6f8fa;padding:8px'>{WebUtility.HtmlEncode(Pretty(body))}</pre></details>");
        }

        fhirSection = "<h3>FHIR reads (scoped to launched patient)</h3>" +
                      "<table cellpadding='6' style='border-collapse:collapse;border:1px solid #ccc'>" +
                      "<tr><th align='left'>Resource</th><th align='left'>HTTP</th><th align='left'>Result</th></tr>" +
                      rows + "</table>" + details;
    }

    return Html($@"
        <h2>✅ eCW Provider EMR Launch — end-to-end OK</h2>
        <table cellpadding='6' style='border-collapse:collapse'>
          <tr><td><b>Patient context</b></td><td><code>{WebUtility.HtmlEncode(patient ?? "(none)")}</code></td></tr>
          <tr><td><b>Granted scope</b></td><td><code>{WebUtility.HtmlEncode(grantedScope ?? "")}</code></td></tr>
          <tr><td><b>Refresh token</b></td><td>{(hasRefresh ? "✅ yes" : "❌ no")}</td></tr>
          <tr><td><b>Expires in</b></td><td>{expiresIn}s</td></tr>
        </table>
        {fhirSection}
        <details><summary>Raw token response</summary><pre>{WebUtility.HtmlEncode(Pretty(tokenBody))}</pre></details>
        <details><summary>Access token (JWT)</summary><pre style='white-space:pre-wrap;word-break:break-all'>{WebUtility.HtmlEncode(accessToken)}</pre></details>");
});

// --- Landing page -----------------------------------------------------------
app.MapGet("/", () => Html(
    "<h2>eCW Provider EMR Launch POC</h2>" +
    "<p>Running. Now open the eCW dev portal → your <b>Provider EMR</b> app → <b>Launch</b>, " +
    "pick a provider + test patient, and launch.</p>" +
    "<p>Registered endpoints handled here:</p>" +
    "<ul><li><code>GET /api/v1/oauth/launch</code></li><li><code>GET /api/v1/oauth/callback</code></li></ul>"));

// HTTPS is required: eCW's authorize page carries `upgrade-insecure-requests`, so it upgrades the
// http callback to https before the browser hits us. Serving http here would fail the callback at
// the TLS layer ("localhost sent an invalid response") before ASP.NET ever sees the request.
Console.WriteLine("eCW Provider EMR Launch POC listening on https://localhost:5000");
app.Run("https://localhost:5000");

// --- helpers ----------------------------------------------------------------
static string Require(string? value, string key) =>
    string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Missing configuration '{key}' (set it in appsettings.json or user-secrets).") : value;

static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static IResult Html(string body) => Results.Content(
    $"<!doctype html><html><head><meta charset='utf-8'><title>eCW Provider EMR POC</title></head>" +
    $"<body style='font-family:system-ui,sans-serif;max-width:70rem;margin:2rem auto;padding:0 1rem;line-height:1.5'>{body}</body></html>",
    "text/html");

static string Pretty(string json)
{
    try
    {
        using var d = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true });
    }
    catch { return json; }
}

// One-line summary of a FHIR response: for a Bundle, its total + how many entries came back;
// for a single resource, its resourceType; for an OperationOutcome, flags the error.
static string Summarize(string json)
{
    try
    {
        using var d = JsonDocument.Parse(json);
        var root = d.RootElement;
        var resourceType = root.TryGetProperty("resourceType", out var rt) ? rt.GetString() : "?";
        if (resourceType == "Bundle")
        {
            var total = root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1;
            var entries = root.TryGetProperty("entry", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
            return total >= 0 ? $"Bundle · total={total} · returned={entries}" : $"Bundle · returned={entries}";
        }
        if (resourceType == "OperationOutcome") return "OperationOutcome (error/warning)";
        return resourceType ?? "?";
    }
    catch { return "(unparseable body)"; }
}
