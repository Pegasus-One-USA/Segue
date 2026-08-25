using FHIRBridge.Tools.TerminologyServerPoc;

var autoMode = args.Contains("--auto-icd10");
var baseUrl = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? Environment.GetEnvironmentVariable("HAPI_TERMINOLOGY_BASE_URL")
    ?? "http://localhost:8090/fhir";

return autoMode
    ? await RunAutoIcd10Async(baseUrl)
    : await RunDemoAsync(baseUrl);

static async Task<int> RunDemoAsync(string baseUrl)
{
    Console.WriteLine($"Terminology server: {baseUrl}");
    Console.WriteLine();

    var client = new HapiTerminologyClient(baseUrl);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    Console.WriteLine("1. Checking server is reachable (GET /metadata)...");
    if (!await client.PingAsync(cts.Token))
    {
        Console.WriteLine("  ✗ Could not reach the terminology server. Is the 'hapi-terminology' container running?");
        Console.WriteLine("    Start it with: docker compose up -d hapi-terminology-postgres hapi-terminology");
        return 1;
    }
    Console.WriteLine("  ✓ Server responded.");
    Console.WriteLine();

    Console.WriteLine($"2. Uploading demo ICD-10-CM subset ({Icd10SampleCodeSystem.Concepts.Count} codes)...");
    var uploaded = await client.UploadCodeSystemAsync(
        Icd10SampleCodeSystem.BuildCodeSystemResource(),
        Icd10SampleCodeSystem.ResourceId,
        cts.Token);
    if (!uploaded)
    {
        return 1;
    }
    Console.WriteLine("  ✓ CodeSystem uploaded.");
    Console.WriteLine();

    Console.WriteLine("3. Verifying each code is queryable via $lookup...");
    var allFound = true;
    foreach (var (code, expectedDisplay) in Icd10SampleCodeSystem.Concepts)
    {
        var (found, display) = await client.LookupAsync(Icd10SampleCodeSystem.SystemUrl, code, cts.Token);
        if (found && display == expectedDisplay)
        {
            Console.WriteLine($"  ✓ {code,-10} -> {display}");
        }
        else if (found)
        {
            Console.WriteLine($"  ~ {code,-10} -> {display} (expected \"{expectedDisplay}\")");
        }
        else
        {
            Console.WriteLine($"  ✗ {code,-10} -> not found");
            allFound = false;
        }
    }

    Console.WriteLine();
    Console.WriteLine(allFound
        ? "Done — all demo codes loaded and queryable."
        : "Done — some codes were not found; see output above.");

    return allFound ? 0 : 1;
}

static async Task<int> RunAutoIcd10Async(string baseUrl)
{
    Console.WriteLine("=== Fully automatic ICD-10-CM load (no manual download/upload) ===");
    Console.WriteLine($"Terminology server: {baseUrl}");
    Console.WriteLine();

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    Console.WriteLine("1. Checking server is reachable (GET /metadata)...");
    var pingClient = new HapiTerminologyClient(baseUrl);
    using (var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
    {
        if (!await pingClient.PingAsync(pingCts.Token))
        {
            Console.WriteLine("  ✗ Could not reach the terminology server. Is the 'hapi-terminology' container running?");
            return 1;
        }
    }
    Console.WriteLine("  ✓ Server responded.");
    Console.WriteLine();

    Console.WriteLine("2. Auto-downloading the real, official CMS/CDC ICD-10-CM release (no credentials needed)...");
    using var downloadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var progress = new Progress<string>(m => Console.WriteLine($"   {m}"));
    IReadOnlyList<Icd10FullDataSource.Concept> concepts;
    using (var downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
    {
        concepts = await Icd10FullDataSource.DownloadAndParseAsync(downloadHttp, progress, downloadCts.Token);
    }
    Console.WriteLine();

    // Long timeout: loading ~90k+ concepts into HAPI's JPA store is slow the first time.
    var client = new HapiTerminologyClient(baseUrl, TimeSpan.FromMinutes(15));

    Console.WriteLine("3. Retiring the small demo subset (same canonical system URL, being superseded)...");
    using (var retireCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
    {
        await client.DeleteCodeSystemAsync(Icd10SampleCodeSystem.ResourceId, retireCts.Token);
    }
    Console.WriteLine("  ✓ Done.");
    Console.WriteLine();

    Console.WriteLine($"4. Uploading the full CodeSystem ({concepts.Count:N0} concepts) — this can take a few minutes...");
    using (var uploadCts = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
    {
        var uploaded = await client.UploadCodeSystemAsync(
            Icd10SampleCodeSystem.BuildFullCodeSystemResource(concepts),
            Icd10SampleCodeSystem.FullResourceId,
            uploadCts.Token);
        if (!uploaded)
        {
            return 1;
        }
    }
    Console.WriteLine("  ✓ Full CodeSystem uploaded.");
    Console.WriteLine();

    Console.WriteLine("5. Spot-checking real codes pulled from the official file just downloaded...");
    var sampleChecks = concepts
        .Where(c => c.Billable)
        .OrderBy(_ => Guid.NewGuid())
        .Take(5)
        .Concat(new[] { concepts.First(c => c.Code == "E11.9") })
        .ToList();

    var allFound = true;
    using var lookupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    foreach (var concept in sampleChecks)
    {
        var (found, display) = await client.LookupAsync(Icd10SampleCodeSystem.SystemUrl, concept.Code, lookupCts.Token);
        if (found)
        {
            Console.WriteLine($"  ✓ {concept.Code,-10} -> {display}");
        }
        else
        {
            Console.WriteLine($"  ✗ {concept.Code,-10} -> not found (expected \"{concept.Display}\")");
            allFound = false;
        }
    }

    stopwatch.Stop();
    Console.WriteLine();
    Console.WriteLine($"Total time: {stopwatch.Elapsed:mm\\:ss}");
    Console.WriteLine(allFound
        ? "Done — the full official ICD-10-CM release was downloaded, parsed, and loaded with zero manual file handling."
        : "Done, but some spot-checked codes were not found — see output above.");

    return allFound ? 0 : 1;
}
