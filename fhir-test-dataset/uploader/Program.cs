using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FhirTestUploader;

/// <summary>
/// FHIR R4 test-dataset uploader for the Segue HAPI server (and Aidbox / Medplum).
///
/// Modes:
///   --mode individual    Upload resource files one by one in dependency order (PUT, idempotent).
///   --mode transaction   Upload the transaction bundles for the chosen flavor.
///   --mode validation    Validate references locally (no network calls).
///   --mode dry-run       Print exactly what WOULD be uploaded; make no calls.
///
/// Flavor:
///   --flavor hapi        Use bundles/hapi-aidbox (PUT + deterministic ids). Default.
///   --flavor medplum     Use bundles/medplum   (POST + urn:uuid + ifNoneExist).
///
/// The program never runs automatically on build. It only acts when invoked with a mode.
/// Auth: anonymous by default; a Bearer token may be supplied via the FHIR_BEARER_TOKEN
/// environment variable (preferred) or appsettings Auth:BearerToken. Credentials are never
/// hardcoded.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = GetArg(args, "--mode") ?? "dry-run";
        var flavor = (GetArg(args, "--flavor") ?? "hapi").ToLowerInvariant();
        var baseOverride = GetArg(args, "--base");

        AppConfig config;
        try
        {
            config = AppConfig.Load();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load configuration: {ex.Message}");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(baseOverride))
            config.BaseUrl = baseOverride!;

        var datasetRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config.DatasetRoot));
        Console.WriteLine($"FHIR test uploader | mode={mode} flavor={flavor}");
        Console.WriteLine($"Base URL   : {config.BaseUrl}");
        Console.WriteLine($"Dataset    : {datasetRoot}");
        Console.WriteLine();

        try
        {
            switch (mode.ToLowerInvariant())
            {
                case "validation":
                    return RunValidation(datasetRoot);
                case "dry-run":
                    return RunDryRun(datasetRoot, flavor, config);
                case "individual":
                    return await RunIndividualAsync(datasetRoot, config);
                case "transaction":
                    return await RunTransactionAsync(datasetRoot, flavor, config);
                default:
                    Console.Error.WriteLine($"Unknown --mode '{mode}'. Use individual | transaction | validation | dry-run.");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex}");
            return 1;
        }
    }

    // -----------------------------------------------------------------------
    // Dependency creation order (Organization first ... DocumentReference last)
    // -----------------------------------------------------------------------
    private static readonly string[] DependencyOrder =
    {
        "Organization", "Location", "Practitioner", "PractitionerRole", "Device",
        "Medication", "Patient", "RelatedPerson", "Coverage", "CareTeam",
        "Encounter", "Condition", "AllergyIntolerance", "Observation",
        "DiagnosticReport", "Procedure", "ServiceRequest", "MedicationRequest",
        "Immunization", "CarePlan", "DocumentReference"
    };

    // -----------------------------------------------------------------------
    // validation mode - local reference check, no network
    // -----------------------------------------------------------------------
    private static int RunValidation(string datasetRoot)
    {
        var resourcesDir = Path.Combine(datasetRoot, "resources");
        if (!Directory.Exists(resourcesDir))
        {
            Console.Error.WriteLine($"resources directory not found at {resourcesDir}. Run 'node generate.js' first.");
            return 2;
        }

        var present = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.GetFiles(resourcesDir, "*.json", SearchOption.AllDirectories);
        var parsed = new List<(string path, JsonNode node)>();
        foreach (var f in files)
        {
            var node = JsonNode.Parse(File.ReadAllText(f));
            if (node is null) continue;
            parsed.Add((f, node));
            var type = node["resourceType"]?.GetValue<string>();
            var id = node["id"]?.GetValue<string>();
            if (type is not null && id is not null) present.Add($"{type}/{id}");
        }

        int total = 0, ok = 0, broken = 0;
        foreach (var (path, node) in parsed)
        {
            foreach (var reference in CollectReferences(node))
            {
                if (reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                var key = TailKey(reference);
                if (present.Contains(key)) ok++;
                else
                {
                    broken++;
                    Console.WriteLine($"BROKEN  {Path.GetFileName(path)} -> {reference}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Resources: {parsed.Count} | literal references: {total} | resolved: {ok} | broken: {broken}");
        Console.WriteLine(broken == 0
            ? "VALID: all literal references resolve within the dataset."
            : $"INVALID: {broken} broken reference(s).");
        return broken == 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------
    // dry-run mode - print plan, no calls
    // -----------------------------------------------------------------------
    private static int RunDryRun(string datasetRoot, string flavor, AppConfig config)
    {
        Console.WriteLine("DRY RUN - no network calls will be made.");
        Console.WriteLine();

        var files = EnumerateResourceFilesInOrder(datasetRoot).ToList();
        Console.WriteLine($"[individual] would PUT {files.Count} resource files in dependency order:");
        foreach (var f in files)
            Console.WriteLine($"  PUT {config.BaseUrl}/{RelativeResourceUrl(f)}");

        Console.WriteLine();
        var bundleDir = BundleDir(datasetRoot, flavor);
        if (Directory.Exists(bundleDir))
        {
            var bundles = SelectBundles(bundleDir);
            Console.WriteLine($"[transaction:{flavor}] would POST {bundles.Count} transaction bundle(s) to {config.BaseUrl}:");
            foreach (var b in bundles)
                Console.WriteLine($"  POST (transaction Bundle) {Path.GetFileName(b)}");
        }
        return 0;
    }

    // -----------------------------------------------------------------------
    // individual mode - PUT each file in dependency order, idempotent + resume
    // -----------------------------------------------------------------------
    private static async Task<int> RunIndividualAsync(string datasetRoot, AppConfig config)
    {
        using var client = BuildClient(config);
        var statePath = Path.Combine(AppContext.BaseDirectory, config.StateFile);
        var failedPath = Path.Combine(AppContext.BaseDirectory, config.FailedResourceLog);
        var done = LoadState(statePath);

        var files = EnumerateResourceFilesInOrder(datasetRoot).ToList();
        int success = 0, skipped = 0, failed = 0;

        foreach (var file in files)
        {
            var url = RelativeResourceUrl(file); // "Type/id"
            if (done.Contains(url))
            {
                skipped++;
                Console.WriteLine($"SKIP (already created) {url}");
                continue;
            }

            var body = await File.ReadAllTextAsync(file);
            var result = await SendWithRetryAsync(client, HttpMethod.Put, url, body, config);
            if (result.Success)
            {
                success++;
                done.Add(url);
                SaveState(statePath, done);
                Console.WriteLine($"OK   [{(int)result.Status}] PUT {url}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL [{(int)result.Status}] PUT {url} - {result.Message}");
                await File.AppendAllTextAsync(failedPath,
                    $"{DateTime.UtcNow:o}\tPUT\t{url}\t{(int)result.Status}\t{Escape(result.Message)}{Environment.NewLine}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"individual complete: success={success} skipped={skipped} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------
    // transaction mode - POST each bundle for the chosen flavor
    // -----------------------------------------------------------------------
    private static async Task<int> RunTransactionAsync(string datasetRoot, string flavor, AppConfig config)
    {
        using var client = BuildClient(config);
        var bundleDir = BundleDir(datasetRoot, flavor);
        if (!Directory.Exists(bundleDir))
        {
            Console.Error.WriteLine($"Bundle directory not found: {bundleDir}");
            return 2;
        }

        var failedPath = Path.Combine(AppContext.BaseDirectory, config.FailedResourceLog);
        var bundles = SelectBundles(bundleDir);

        int success = 0, failed = 0;
        foreach (var bundle in bundles)
        {
            var body = await File.ReadAllTextAsync(bundle);
            // POST a transaction Bundle to the base URL.
            var result = await SendWithRetryAsync(client, HttpMethod.Post, string.Empty, body, config);
            if (result.Success)
            {
                success++;
                Console.WriteLine($"OK   [{(int)result.Status}] transaction {Path.GetFileName(bundle)}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL [{(int)result.Status}] transaction {Path.GetFileName(bundle)} - {result.Message}");
                await File.AppendAllTextAsync(failedPath,
                    $"{DateTime.UtcNow:o}\tTRANSACTION\t{Path.GetFileName(bundle)}\t{(int)result.Status}\t{Escape(result.Message)}{Environment.NewLine}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"transaction complete ({flavor}): success={success} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------
    // HTTP helpers
    // -----------------------------------------------------------------------
    private static HttpClient BuildClient(AppConfig config)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(config.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

        // Auth precedence: Bearer token (env or appsettings) wins; otherwise Basic
        // auth from FHIR_BASIC_AUTH ("client-id:secret", e.g. an Aidbox Client). Both
        // are read from the environment so no secret is ever written to disk.
        var token = Environment.GetEnvironmentVariable("FHIR_BEARER_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) token = config.BearerToken;
        var basic = Environment.GetEnvironmentVariable("FHIR_BASIC_AUTH");

        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (!string.IsNullOrWhiteSpace(basic))
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(basic));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        return client;
    }

    private readonly record struct SendResult(bool Success, HttpStatusCode Status, string Message);

    private static async Task<SendResult> SendWithRetryAsync(
        HttpClient client, HttpMethod method, string relativeUrl, string body, AppConfig config)
    {
        HttpStatusCode lastStatus = 0;
        string lastMessage = string.Empty;

        for (int attempt = 0; attempt <= config.MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, relativeUrl)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/fhir+json")
                };
                using var response = await client.SendAsync(request);
                lastStatus = response.StatusCode;

                if (response.IsSuccessStatusCode)
                    return new SendResult(true, response.StatusCode, "ok");

                lastMessage = await SummarizeErrorAsync(response);

                // Retry only on transient conditions.
                if (!IsTransient(response.StatusCode) || attempt == config.MaxRetries)
                    return new SendResult(false, response.StatusCode, lastMessage);
            }
            catch (TaskCanceledException)
            {
                lastMessage = "request timed out";
                if (attempt == config.MaxRetries) return new SendResult(false, lastStatus, lastMessage);
            }
            catch (HttpRequestException ex)
            {
                lastMessage = ex.Message;
                if (attempt == config.MaxRetries) return new SendResult(false, lastStatus, lastMessage);
            }

            var delay = TimeSpan.FromSeconds(config.RetryBaseDelaySeconds * Math.Pow(2, attempt));
            Console.WriteLine($"  ...retry {attempt + 1}/{config.MaxRetries} after {delay.TotalSeconds:0}s ({lastMessage})");
            await Task.Delay(delay);
        }

        return new SendResult(false, lastStatus, lastMessage);
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||
        status == (HttpStatusCode)429 ||
        (int)status >= 500;

    private static async Task<string> SummarizeErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text)) return response.ReasonPhrase ?? "error";
            var node = JsonNode.Parse(text);
            // FHIR OperationOutcome.issue[].diagnostics
            var issues = node?["issue"] as JsonArray;
            if (issues is { Count: > 0 })
            {
                var first = issues[0];
                var diag = first?["diagnostics"]?.GetValue<string>()
                           ?? first?["details"]?["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(diag)) return diag!;
            }
            return text.Length > 300 ? text[..300] : text;
        }
        catch
        {
            return response.ReasonPhrase ?? "error";
        }
    }

    // -----------------------------------------------------------------------
    // File / reference utilities
    // -----------------------------------------------------------------------
    private static IEnumerable<string> EnumerateResourceFilesInOrder(string datasetRoot)
    {
        var resourcesDir = Path.Combine(datasetRoot, "resources");
        if (!Directory.Exists(resourcesDir)) yield break;

        foreach (var type in DependencyOrder)
        {
            var dir = Path.Combine(resourcesDir, type);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
                yield return f;
        }
    }

    private static string RelativeResourceUrl(string file)
    {
        // resources/<Type>/<id>.json -> "<Type>/<id>"
        var type = Path.GetFileName(Path.GetDirectoryName(file)!);
        var id = Path.GetFileNameWithoutExtension(file);
        return $"{type}/{id}";
    }

    private static string BundleDir(string datasetRoot, string flavor)
    {
        var sub = flavor == "medplum" ? "medplum" : "hapi-aidbox";
        return Path.Combine(datasetRoot, "bundles", sub);
    }

    // A consolidated single-transaction bundle (filename contains "all") is preferred: it
    // loads the whole graph in ONE transaction so references resolve regardless of entry
    // order — the only reliable shape for strict-RI servers (HAPI, Aidbox). Fall back to the
    // split foundation/per-patient bundles only when no "all" bundle exists.
    private static List<string> SelectBundles(string bundleDir)
    {
        var allFiles = Directory.GetFiles(bundleDir, "*.json");
        var consolidated = allFiles
            .Where(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Contains("all"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        return consolidated.Count > 0 ? consolidated : allFiles.OrderBy(BundleSortKey).ToList();
    }

    private static int BundleSortKey(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("foundation")) return 0;
        if (name.Contains("all")) return 1;
        if (name.Contains("admission")) return 3;
        return 2; // per-patient bundles
    }

    private static IEnumerable<string> CollectReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kvp in obj)
                {
                    if (kvp.Key == "reference" && kvp.Value is JsonValue v && v.TryGetValue(out string? s) && s is not null)
                        yield return s;
                    else
                        foreach (var r in CollectReferences(kvp.Value)) yield return r;
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    foreach (var r in CollectReferences(item)) yield return r;
                break;
        }
    }

    private static string TailKey(string reference)
    {
        var parts = reference.Split('/');
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : reference;
    }

    // -----------------------------------------------------------------------
    // Resume-state persistence (set of "Type/id" already created)
    // -----------------------------------------------------------------------
    private static HashSet<string> LoadState(string statePath)
    {
        if (!File.Exists(statePath)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(File.ReadAllText(statePath));
            return arr is null ? new HashSet<string>(StringComparer.Ordinal)
                               : new HashSet<string>(arr, StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void SaveState(string statePath, HashSet<string> done) =>
        File.WriteAllText(statePath, JsonSerializer.Serialize(done.ToArray(),
            new JsonSerializerOptions { WriteIndented = true }));

    // -----------------------------------------------------------------------
    // arg + misc helpers
    // -----------------------------------------------------------------------
    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    private static string Escape(string s) => s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
}

/// <summary>Strongly typed configuration loaded from appsettings.json (+ env override).</summary>
public sealed class AppConfig
{
    public string BaseUrl { get; set; } = "https://segue.pegasusone.com:7011/fhir";
    public int TimeoutSeconds { get; init; } = 120;
    public int BatchSize { get; init; } = 20;
    public int MaxRetries { get; init; } = 4;
    public int RetryBaseDelaySeconds { get; init; } = 2;
    public string DatasetRoot { get; init; } = "..";
    public string FailedResourceLog { get; init; } = "failed-resources.log";
    public string StateFile { get; init; } = "upload-state.json";
    public string BearerToken { get; init; } = string.Empty;

    public static AppConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new AppConfig();

        var root = JsonNode.Parse(File.ReadAllText(path));
        var fhir = root?["Fhir"];
        var auth = root?["Auth"];

        string Str(JsonNode? n, string key, string fallback) =>
            n?[key]?.GetValue<string>() ?? fallback;
        int Int(JsonNode? n, string key, int fallback) =>
            n?[key] is JsonValue v && v.TryGetValue(out int i) ? i : fallback;

        return new AppConfig
        {
            BaseUrl = Str(fhir, "BaseUrl", "https://segue.pegasusone.com:7011/fhir"),
            TimeoutSeconds = Int(fhir, "TimeoutSeconds", 120),
            BatchSize = Int(fhir, "BatchSize", 20),
            MaxRetries = Int(fhir, "MaxRetries", 4),
            RetryBaseDelaySeconds = Int(fhir, "RetryBaseDelaySeconds", 2),
            DatasetRoot = Str(fhir, "DatasetRoot", ".."),
            FailedResourceLog = Str(fhir, "FailedResourceLog", "failed-resources.log"),
            StateFile = Str(fhir, "StateFile", "upload-state.json"),
            BearerToken = Str(auth, "BearerToken", string.Empty)
        };
    }
}
