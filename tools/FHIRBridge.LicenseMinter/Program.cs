using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Tools.LicenseMinter;

/// <summary>
/// Standalone signed-license minting tool — see the .csproj header comment for why this deliberately has no
/// project references into the main FHIRBridge solution. Mints a compact JWS matching exactly the claims
/// shape <c>SignedLicenseValidator</c> (src/FHIRBridge.Infrastructure/Licensing) expects, signs it with an
/// ES256 (ECDSA P-256) private key, and prints the token to stdout.
///
/// Deviation from the spec's bare arg list: producing a valid token needs BOTH a "sub" claim (a stable
/// customer id/slug) and a "customerName" claim (a display name) — so this tool accepts an optional
/// <c>--customer-name</c> in addition to the required <c>--customer</c>, defaulting it to the same value as
/// <c>--customer</c> when omitted so the single-arg case still works.
/// </summary>
internal static class Program
{
    // DEV-ONLY private key — the exact match to the public key embedded in
    // src/FHIRBridge.Infrastructure/Licensing/LicensePublicKey.cs. Checked in ONLY so this tool is
    // immediately usable via --dev-key during development/testing. NEVER use this for a real customer
    // license: before shipping to a first real customer, generate a production ECDSA P-256 keypair, keep
    // the private key ONLY in Key Vault, and pass it to this tool via --private-key-file instead.
    private const string DevPrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgzNfRUjAwdWWhtF6HDTFMjzkpc6ix4tAt0KpVuBS1H+mhRANCAAQnMOZWXkeX06SoZyVY2NFCQjAPD9dXCmyoZChc3n59+CHbwMLDg8lzcuh/60NbOQzfHLB/fGxuUpTM+n5l2sJr";

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);

            if (!options.TryGetValue("customer", out var customerId) || string.IsNullOrWhiteSpace(customerId))
            {
                Console.Error.WriteLine("Missing required --customer <id> (used as the 'sub' claim).");
                PrintUsage();
                return 1;
            }

            if (!options.TryGetValue("expires", out var expiresRaw) ||
                !DateTime.TryParse(
                    expiresRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expiresUtc))
            {
                Console.Error.WriteLine("Missing/invalid required --expires <yyyy-MM-dd> date.");
                PrintUsage();
                return 1;
            }

            using var ecdsa = LoadPrivateKey(options);

            var customerName = options.GetValueOrDefault("customer-name", customerId);
            var edition = options.GetValueOrDefault("edition", "standard");
            var features = (options.GetValueOrDefault("features") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var allowedSourceTypes = ParseNullableStringList(options, "allowed-source-types");
            var allowedHospitals = ParseAllowedHospitals(options, "allowed-hospitals");
            var allowedResourceTypes = ParseNullableStringList(options, "allowed-resource-types");
            var allowedDestinationTypes = ParseNullableStringList(options, "allowed-destination-types");
            var activationWindowMinutes = ParseNullableInt(options, "activation-window-minutes");

            var nowUtc = DateTime.UtcNow;

            var payloadJson = JsonSerializer.Serialize(new
            {
                iss = "pegasusone",
                sub = customerId,
                customerName,
                edition,
                nbf = ToUnixSeconds(nowUtc),
                exp = ToUnixSeconds(expiresUtc),
                // Absent when --activation-window-minutes is omitted — SignedLicenseValidator only enforces
                // this claim when present, so an unset window means "no activation deadline", same as every
                // other optional claim in this payload.
                activateByUtc = activationWindowMinutes.HasValue
                    ? ToUnixSeconds(nowUtc.AddMinutes(activationWindowMinutes.Value))
                    : (long?)null,
                maxUsers = ParseIntOrUnlimited(options, "max-users"),
                maxWorkflows = ParseIntOrUnlimited(options, "max-workflows"),
                maxSourceConnections = ParseIntOrUnlimited(options, "max-source-connections"),
                features,
                allowedSourceTypes,
                allowedHospitals,
                maxProcessedRecordsPerMonth = ParseIntOrUnlimited(options, "max-processed-records-per-month"),
                allowedResourceTypes,
                allowedDestinationTypes,
                maxSuccessfulWorkflowExecutionsPerMonth = ParseIntOrUnlimited(options, "max-successful-workflow-executions-per-month"),
                // Absent when --request-key is omitted — SignedLicenseValidator/LicenseService.ApplyAsync
                // only checks this claim against the applying install's own LicenseRequest.UniqueKey when
                // it's present, so an unset key means "not tied to a specific request" (every license
                // minted before this feature existed keeps working unchanged).
                requestKey = options.GetValueOrDefault("request-key"),
            });

            var credentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256);
            var token = new JsonWebTokenHandler().CreateToken(payloadJson, credentials);

            Console.WriteLine(token);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to mint license: {ex.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            if (string.Equals(key, "dev-key", StringComparison.OrdinalIgnoreCase))
            {
                result[key] = "true";
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for --{key}.");
            }

            result[key] = args[++i];
        }

        return result;
    }

    private static ECDsa LoadPrivateKey(Dictionary<string, string> options)
    {
        var useDevKey = options.ContainsKey("dev-key");
        var privateKeyFile = options.GetValueOrDefault("private-key-file");

        if (!useDevKey && string.IsNullOrWhiteSpace(privateKeyFile))
        {
            throw new ArgumentException("Provide either --private-key-file <path> or --dev-key.");
        }

        var ecdsa = ECDsa.Create();
        if (useDevKey)
        {
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(DevPrivateKeyPkcs8Base64), out _);
            return ecdsa;
        }

        var keyText = File.ReadAllText(privateKeyFile!).Trim();
        if (keyText.Contains("BEGIN", StringComparison.Ordinal))
        {
            ecdsa.ImportFromPem(keyText);
        }
        else
        {
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(keyText), out _);
        }

        return ecdsa;
    }

    /// <summary>Absent/blank option, or an explicit "-1", both mean unlimited for that dimension — matches
    /// <c>LicenseLimits.Unlimited</c>/<c>SignedLicenseValidator.GetIntOrUnlimited</c>'s convention on the
    /// verifying side.</summary>
    private const int Unlimited = -1;

    private static int ParseIntOrUnlimited(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return Unlimited;
        }

        return int.Parse(raw, CultureInfo.InvariantCulture);
    }

    /// <summary>Absent/blank option means null (no activation deadline) — unlike
    /// <see cref="ParseIntOrUnlimited"/>, there's no "-1 means unlimited" convention here, since an
    /// unenforced activation window and an infinite one are the same thing.</summary>
    private static int? ParseNullableInt(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.Parse(raw, CultureInfo.InvariantCulture);
    }

    /// <summary>Parses a comma-separated list option (e.g. <c>--allowed-source-types epic,healow</c>) into
    /// a string array, or <c>null</c> when the option is absent/blank — same "absent means unrestricted"
    /// convention as the four numeric limits above, rather than <c>--features</c>'s "absent means empty".</summary>
    private static string[]? ParseNullableStringList(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Length > 0 ? values : null;
    }

    /// <summary>
    /// Parses <c>--allowed-hospitals</c>: semicolon-separated hospital entries, each a pipe-separated
    /// <c>vendor|baseUrl[|displayName]</c> triple — e.g.
    /// <c>--allowed-hospitals "Epic|https://epic.mercy.example/fhir/r4|Mercy Main;Healow|https://ecw.mercy.example/fhir|Mercy Clinic"</c>.
    /// Pipe (rather than colon) separates the three parts because a FHIR base URL always contains colons
    /// (the <c>https://</c> scheme); displayName is optional and may be omitted entirely. Returns <c>null</c>
    /// when the option is absent/blank — absent means "any hospital allowed", same convention as
    /// <see cref="ParseNullableStringList"/>.
    /// </summary>
    private static object[]? ParseAllowedHospitals(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var hospitals = raw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry =>
            {
                var parts = entry.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                {
                    throw new ArgumentException(
                        $"Invalid --{key} entry '{entry}' — expected 'vendor|baseUrl[|displayName]'.");
                }

                return new
                {
                    vendor = parts[0],
                    baseUrl = parts[1],
                    displayName = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null,
                };
            })
            .Cast<object>()
            .ToArray();

        return hospitals.Length > 0 ? hospitals : null;
    }

    private static long ToUnixSeconds(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: dotnet run --project tools/FHIRBridge.LicenseMinter -- --customer <id> --expires <yyyy-MM-dd> " +
            "[--customer-name <name>] [--edition <edition>] [--max-users <n>] [--max-workflows <n>] " +
            "[--max-source-connections <n>] [--features a,b,c] " +
            "[--allowed-source-types epic,healow] [--allowed-hospitals <entries>] " +
            "[--max-processed-records-per-month <n>] " +
            "[--allowed-resource-types Patient,Observation] [--allowed-destination-types SqlServer,Sftp] " +
            "[--max-successful-workflow-executions-per-month <n>] " +
            "[--activation-window-minutes <n>] [--request-key <key>] " +
            "(--dev-key | --private-key-file <path>)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  --activation-window-minutes  Once generated, this key must be applied (POST /api/v1/license)");
        Console.Error.WriteLine("                          within this many minutes or it's rejected as expired — a freshly");
        Console.Error.WriteLine("                          issued key that's never installed can't sit around indefinitely.");
        Console.Error.WriteLine("                          Omit for no activation deadline. Does NOT affect an already-applied,");
        Console.Error.WriteLine("                          currently-running license — see SignedLicenseValidator's remarks.");
        Console.Error.WriteLine("  --request-key           The customer's LicenseRequest.UniqueKey, from their license");
        Console.Error.WriteLine("                          request submission (direct API call or the manual encoded blob).");
        Console.Error.WriteLine("                          Ties this license to that specific request: LicenseService.ApplyAsync");
        Console.Error.WriteLine("                          refuses to apply it on any install whose own stored key differs. Omit");
        Console.Error.WriteLine("                          if this license wasn't minted against a customer's request.");
        Console.Error.WriteLine("  --max-users / --max-workflows / --max-source-connections / --max-processed-records-per-month /");
        Console.Error.WriteLine("  --max-successful-workflow-executions-per-month");
        Console.Error.WriteLine("                          Omit, or pass -1 explicitly, for unlimited.");
        Console.Error.WriteLine("  --allowed-source-types  Comma-separated SourceSystemType names this license permits");
        Console.Error.WriteLine("                          configuring (e.g. 'Epic,Healow'). Omit for all types allowed.");
        Console.Error.WriteLine("  --allowed-hospitals     Semicolon-separated hospital entries, each a pipe-separated");
        Console.Error.WriteLine("                          'vendor|baseUrl[|displayName]' triple, e.g.:");
        Console.Error.WriteLine("                            --allowed-hospitals \"Epic|https://epic.mercy.example/fhir/r4|Mercy Main;Healow|https://ecw.mercy.example/fhir|Mercy Clinic\"");
        Console.Error.WriteLine("                          Pipe (not colon) separates the parts because a FHIR base URL");
        Console.Error.WriteLine("                          always contains colons. displayName is optional. Omit the whole");
        Console.Error.WriteLine("                          option for any hospital allowed.");
        Console.Error.WriteLine("  --allowed-resource-types  Comma-separated FHIR resource type names this license permits");
        Console.Error.WriteLine("                            processing (e.g. 'Patient,Observation'). Omit for all types allowed.");
        Console.Error.WriteLine("  --allowed-destination-types  Comma-separated DestinationType names this license permits");
        Console.Error.WriteLine("                                writing to (e.g. 'SqlServer,Sftp'). Omit for all types allowed.");
    }
}
