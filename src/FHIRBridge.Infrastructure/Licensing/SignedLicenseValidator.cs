using System.Security.Cryptography;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Licensing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// Verifies a compact-JWS signed license token against <see cref="LicensePublicKey.PublicKeyBase64"/> (ES256)
/// and parses its claims into a <see cref="LicenseStatus"/>. Stateless from the caller's point of view (no
/// instance, no DI setup) — safe to call from a singleton (<see cref="LicenseService"/>) — but internally
/// keeps ONE process-lifetime <see cref="ECDsa"/> for the public key (see <see cref="PublicKey"/>) rather
/// than importing/disposing a fresh one per call. That's deliberate, not an optimization: creating-then-
/// disposing an ECDsa per call was tried first and caused intermittent, non-deterministic validation
/// failures — Microsoft.IdentityModel.Tokens' process-wide <c>CryptoProviderFactory.Default</c> caches
/// <c>SignatureProvider</c>s keyed by the key's content (not object identity), so the very next call using
/// the same key material could be handed back a cached provider wrapping the PREVIOUS call's now-disposed
/// ECDsa, and fail. Keeping this key alive for the process's lifetime — the same pattern ASP.NET Core's own
/// JWT bearer middleware uses for its one configured <c>IssuerSigningKey</c>, reused concurrently across
/// every request — avoids that entirely. Verification (unlike signing) does not mutate key state, so
/// concurrent callers sharing this one instance is safe.
///
/// Design choice (documented here since the spec asked for one): a token whose <c>nbf</c> is still in the
/// future is reported as <see cref="LicenseState.Invalid"/>, not <see cref="LicenseState.Grace"/>.
/// <see cref="LicenseState.Grace"/> is reserved for a possible future short window AFTER expiry; "not yet
/// valid" is a different situation (clock skew, or a token applied too early) that this stage treats the
/// same as any other verification failure — no claims are reported, just a short diagnostic.
/// </summary>
public static class SignedLicenseValidator
{
    /// <summary>The only issuer a real PegasusOne-minted license ever carries — see
    /// <c>tools/FHIRBridge.LicenseMinter</c>.</summary>
    private const string ExpectedIssuer = "pegasusone";

    /// <summary>Small allowance for clock drift between the machine that minted the token and the one
    /// validating it, applied to both the <c>nbf</c> and <c>exp</c> checks below.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Lazily-imported, never-disposed ECDsa for <see cref="LicensePublicKey.PublicKeyBase64"/> —
    /// see this class's remarks for why it's long-lived rather than per-call. A malformed embedded key
    /// throws once here; that exception is cached by <see cref="Lazy{T}"/> and reported identically (as
    /// <see cref="LicenseState.Invalid"/>) on every call, which is the correct behavior for a fixed,
    /// compiled-in constant that either loads or doesn't.</summary>
    private static readonly Lazy<ECDsa> PublicKey = new(() =>
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(LicensePublicKey.PublicKeyBase64), out _);
        return ecdsa;
    });

    /// <summary>
    /// Never throws — any failure (malformed input, bad/tampered signature, wrong issuer, garbage string)
    /// comes back as <see cref="LicenseState.Invalid"/> with <see cref="LicenseStatus.InvalidReason"/> set.
    /// Equivalent to <c>Validate(licenseToken, enforceActivationWindow: false)</c> — see that overload's
    /// remarks for why re-validating an already-applied license (every process restart) must never enforce
    /// the activation window.
    /// </summary>
    public static LicenseStatus Validate(string? licenseToken) => Validate(licenseToken, enforceActivationWindow: false);

    /// <summary>
    /// Same as <see cref="Validate(string?)"/>, plus one more check when <paramref name="enforceActivationWindow"/>
    /// is true: a token carrying an <c>activateByUtc</c> claim (see <c>tools/FHIRBridge.LicenseMinter</c>'s
    /// <c>--activation-window-minutes</c>) is reported <see cref="LicenseState.Invalid"/> if the current time
    /// is past that deadline — the license key was generated but never applied within its allotted window.
    /// A token with no <c>activateByUtc</c> claim at all is never affected by this check (older licenses, or
    /// ones minted without a window, keep working exactly as before).
    ///
    /// Pass <c>true</c> ONLY from the "apply a new license" path (<c>LicenseService.ApplyAsync</c>) — NEVER
    /// from the "re-resolve the already-applied license on process startup" path
    /// (<c>LicenseService.ReloadAsync</c>). The activation window is a one-time gate on getting a freshly
    /// minted key installed in the first place; once a license is active and stored, re-checking this same
    /// deadline on every future restart would eventually and permanently brick an otherwise perfectly valid,
    /// already-running license the moment enough real time passed — which is not what an "activation window"
    /// is supposed to mean.
    /// </summary>
    public static LicenseStatus Validate(string? licenseToken, bool enforceActivationWindow)
    {
        if (string.IsNullOrWhiteSpace(licenseToken))
        {
            return Invalid("No license token was provided.");
        }

        ECDsa ecdsa;
        try
        {
            ecdsa = PublicKey.Value;
        }
        catch (Exception ex)
        {
            // Failing to load OUR OWN embedded public key is a configuration bug, not a bad token — but it
            // must still never throw out of this method, so it's reported the same way as any other failure.
            return Invalid($"License public key could not be loaded: {ex.Message}");
        }

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new ECDsaSecurityKey(ecdsa),
            ValidAlgorithms = new[] { SecurityAlgorithms.EcdsaSha256 },
            ValidateIssuer = true,
            ValidIssuer = ExpectedIssuer,
            ValidateAudience = false,
            // nbf/exp are validated manually in BuildStatus below, so an expired license still comes
            // back with its claims parsed (State = Expired) instead of a bare validation failure.
            ValidateLifetime = false,
            RequireExpirationTime = false,
            RequireSignedTokens = true,
        };

        TokenValidationResult result;
        try
        {
            result = new JsonWebTokenHandler()
                .ValidateTokenAsync(licenseToken, validationParameters)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return Invalid($"License token could not be parsed: {Truncate(ex.Message)}");
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jsonWebToken)
        {
            var reason = result.Exception?.Message ?? "Signature or issuer validation failed.";
            return Invalid(Truncate(reason));
        }

        try
        {
            return BuildStatus(jsonWebToken, enforceActivationWindow);
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders: claim parsing below is written to never throw, but a signature-valid
            // token with a claims shape this parser doesn't expect must still come back as Invalid, not
            // propagate out of this method.
            return Invalid($"License claims could not be parsed: {Truncate(ex.Message)}");
        }
    }

    private static LicenseStatus BuildStatus(JsonWebToken jsonWebToken, bool enforceActivationWindow)
    {
        var nowUtc = DateTime.UtcNow;
        // The claims shape carries "nbf" (not-before) but no separate "iat" — nbf doubles as the
        // license's issued-on timestamp for reporting purposes.
        var issuedUtc = GetUnixTimeClaim(jsonWebToken, "nbf");
        var expiresUtc = GetUnixTimeClaim(jsonWebToken, "exp");

        if (issuedUtc.HasValue && nowUtc < issuedUtc.Value - ClockSkew)
        {
            return Invalid("License is not valid yet (nbf is in the future).");
        }

        if (enforceActivationWindow)
        {
            var activateByUtc = GetUnixTimeClaim(jsonWebToken, "activateByUtc");
            if (activateByUtc.HasValue && nowUtc > activateByUtc.Value + ClockSkew)
            {
                return Invalid(
                    "This license key's activation window has expired — it must be applied within the " +
                    "allotted time of being generated. Request a new license key.");
            }
        }

        var limits = new LicenseLimits(
            MaxUsers: GetIntOrUnlimited(jsonWebToken, "maxUsers"),
            MaxWorkflows: GetIntOrUnlimited(jsonWebToken, "maxWorkflows"),
            MaxSourceConnections: GetIntOrUnlimited(jsonWebToken, "maxSourceConnections"),
            AllowedSourceTypes: GetNullableStringArray(jsonWebToken, "allowedSourceTypes"),
            AllowedHospitals: GetAllowedHospitals(jsonWebToken, "allowedHospitals"),
            MaxProcessedRecordsPerMonth: GetIntOrUnlimited(jsonWebToken, "maxProcessedRecordsPerMonth"),
            AllowedResourceTypes: GetNullableStringArray(jsonWebToken, "allowedResourceTypes"),
            AllowedDestinationTypes: GetNullableStringArray(jsonWebToken, "allowedDestinationTypes"),
            MaxSuccessfulWorkflowExecutionsPerMonth: GetIntOrUnlimited(jsonWebToken, "maxSuccessfulWorkflowExecutionsPerMonth"));

        var state = expiresUtc.HasValue && nowUtc > expiresUtc.Value + ClockSkew
            ? LicenseState.Expired
            : LicenseState.Active;

        return new LicenseStatus(
            state,
            GetString(jsonWebToken, "customerName"),
            GetString(jsonWebToken, "edition"),
            issuedUtc,
            expiresUtc,
            limits,
            GetStringArray(jsonWebToken, "features"),
            null);
    }

    private static LicenseStatus Invalid(string reason) =>
        new(LicenseState.Invalid, null, null, null, null, null, Array.Empty<string>(), reason);

    private static string Truncate(string value) => value.Length > 200 ? value[..200] : value;

    // NOTE: deliberately routed through TryGetPayloadValue<object> + a manual pattern match below, rather
    // than TryGetPayloadValue<int?>/<long?> directly. Microsoft.IdentityModel.JsonWebTokens' JsonWebToken
    // (as of the version this project pins) has a quirk where TryGetPayloadValue<T> for a Nullable<T>
    // numeric type returns false for a claim that IS present with a non-null value (only a JSON null or an
    // absent claim comes back true) — verified empirically against 8.14.0. object always round-trips the
    // claim correctly (null, a boxed int, or a boxed long depending on magnitude), so every case below is
    // handled by hand instead of trusting the generic overload.
    private static DateTime? GetUnixTimeClaim(JsonWebToken jsonWebToken, string claimName) =>
        GetNullableLong(jsonWebToken, claimName) is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    /// <summary>Absent/null claim, or an explicit <c>-1</c>, both come back as <see cref="LicenseLimits.Unlimited"/>
    /// — a blank field in the minting UI and an explicit "-1" typed into it mean the same thing.</summary>
    private static int GetIntOrUnlimited(JsonWebToken jsonWebToken, string claimName) =>
        GetNullableLong(jsonWebToken, claimName) is { } value ? unchecked((int)value) : LicenseLimits.Unlimited;

    private static long? GetNullableLong(JsonWebToken jsonWebToken, string claimName)
    {
        if (!jsonWebToken.TryGetPayloadValue<object>(claimName, out var raw))
        {
            return null;
        }

        return raw switch
        {
            null => null,
            long l => l,
            int i => i,
            _ => null,
        };
    }

    private static string? GetString(JsonWebToken jsonWebToken, string claimName) =>
        jsonWebToken.TryGetPayloadValue<string>(claimName, out var value) ? value : null;

    private static IReadOnlyList<string> GetStringArray(JsonWebToken jsonWebToken, string claimName) =>
        jsonWebToken.TryGetPayloadValue<string[]>(claimName, out var values) && values is not null
            ? values
            : Array.Empty<string>();

    /// <summary>Same shape as <see cref="GetStringArray"/>, but returns <c>null</c> (rather than an empty
    /// array) when the claim is absent/empty — the convention <see cref="LicenseLimits.AllowedSourceTypes"/>
    /// uses, so an old license with no such claim reads as "all source types allowed" rather than "none".</summary>
    private static IReadOnlyList<string>? GetNullableStringArray(JsonWebToken jsonWebToken, string claimName) =>
        jsonWebToken.TryGetPayloadValue<string[]>(claimName, out var values) && values is { Length: > 0 }
            ? values
            : null;

    /// <summary>Parses <c>allowedHospitals</c>: an array of <c>{ vendor, baseUrl, displayName }</c> objects.
    /// Absent claim, a non-array claim, an empty array, or an entry missing a non-empty <c>vendor</c>/
    /// <c>baseUrl</c> all fold to that entry being skipped (or the whole claim to <c>null</c>) rather than
    /// throwing — this method must never throw, same contract as every other claim getter in this class.</summary>
    private static IReadOnlyList<AllowedHospital>? GetAllowedHospitals(JsonWebToken jsonWebToken, string claimName)
    {
        if (!jsonWebToken.TryGetPayloadValue<JsonElement>(claimName, out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var hospitals = new List<AllowedHospital>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var vendor = item.TryGetProperty("vendor", out var vendorProp) ? vendorProp.GetString() : null;
            var baseUrl = item.TryGetProperty("baseUrl", out var baseUrlProp) ? baseUrlProp.GetString() : null;
            var displayName = item.TryGetProperty("displayName", out var displayNameProp)
                ? displayNameProp.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(vendor) || string.IsNullOrWhiteSpace(baseUrl))
            {
                continue;
            }

            hospitals.Add(new AllowedHospital(vendor, baseUrl, displayName));
        }

        return hospitals.Count > 0 ? hospitals : null;
    }
}
