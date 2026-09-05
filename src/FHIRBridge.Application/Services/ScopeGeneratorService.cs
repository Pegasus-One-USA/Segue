using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Pure SMART scope generation. Rules:
/// <list type="bullet">
///   <item>Prefix by application type: <c>user/</c> (EHR launch / standalone), <c>patient/</c> (patient), <c>system/</c> (backend).</item>
///   <item>Suffix by SMART scope version: <c>.rs</c> (v2 granular read+search) or <c>.read</c> (v1 coarse).</item>
///   <item>Interactive types add <c>openid fhirUser offline_access</c> plus <c>launch</c> (EHR launch) or <c>launch/patient</c> (patient only). Provider Standalone adds neither. Backend adds none of these.</item>
/// </list>
/// When <c>scopes_supported</c> is supplied, each generated resource scope is checked against it (exact or wildcard
/// match) and any unsupported ones are reported — never silently dropped, so the caller decides.
/// <para>
/// The suffix rule above holds for every vendor that accepts the generic SMART vocabulary. A vendor registered in
/// <see cref="VendorScopeCatalog"/> instead spells each <c>system/</c> read scope with whatever access level it
/// actually advertises for that resource type, and contributes no scope at all for a resource type it advertises
/// none for — those are reported in <see cref="GeneratedScopesDto.UnsupportedScopes"/>. This matters for vendors
/// that fail the WHOLE token request on a single unrecognized scope (eCW and athenahealth both do), where one
/// wrongly-spelled suffix costs every other scope in the request too. Scoped to the <c>system/</c> prefix
/// (Backend) deliberately: the interactive prefixes' generation is left byte-identical.
/// </para>
/// </summary>
public sealed class ScopeGeneratorService : IScopeGeneratorService
{
    public GeneratedScopesDto Generate(
        ApplicationType? applicationType,
        IEnumerable<string> resourceTypes,
        string scopeVersion,
        bool scopeVersionDetected,
        IReadOnlyCollection<string>? supportedScopes,
        SourceSystemType? vendor = null)
    {
        var version = string.Equals(scopeVersion, "v1", StringComparison.OrdinalIgnoreCase) ? "v1" : "v2";
        // Prefix by application type (is-pattern, not a switch on ApplicationType — the engine's dispatch stays in the
        // strategy registry per the architecture rule).
        var prefix = "user";
        if (applicationType is ApplicationType.Patient) prefix = "patient";
        else if (applicationType is ApplicationType.Backend) prefix = "system";
        var suffix = version == "v2" ? "rs" : "read";
        var isInteractive = applicationType is null
            or ApplicationType.EhrLaunch or ApplicationType.Standalone or ApplicationType.Patient;

        var scopes = new List<string>();
        if (isInteractive)
        {
            scopes.Add("openid");
            scopes.Add("fhirUser");
            scopes.Add("offline_access");
            // 'launch' honors an EHR-supplied launch context; 'launch/patient' asks the EHR to show its own
            // patient picker. Provider Standalone needs neither — it gets direct user-level access and does its
            // own patient search inside FHIRBridge.
            if (applicationType is ApplicationType.EhrLaunch)
            {
                scopes.Add("launch");
            }
            else if (applicationType is ApplicationType.Patient)
            {
                scopes.Add("launch/patient");
            }
        }

        var requestedResourceTypes = resourceTypes
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A vendor profile only governs system/ scopes (see the class remarks) — every other prefix, and every
        // vendor with no registered profile, keeps the uniform version suffix exactly as before.
        var profile = prefix == "system" ? VendorScopeCatalog.For(vendor) : null;

        var resourceScopes = new List<string>(requestedResourceTypes.Count);
        // Resource types this vendor advertises no read scope for at all. Reported rather than requested: asking
        // for a scope the authorization server doesn't publish is what fails the entire token request.
        var vendorUnsupported = new List<string>();
        foreach (var resourceType in requestedResourceTypes)
        {
            if (profile is null)
            {
                resourceScopes.Add($"{prefix}/{resourceType}.{suffix}");
            }
            else if (profile.TryGetReadAccessLevel(resourceType, out var vendorAccessLevel))
            {
                resourceScopes.Add($"{prefix}/{resourceType}.{vendorAccessLevel}");
            }
            else
            {
                vendorUnsupported.Add($"{prefix}/{resourceType}.{suffix}");
            }
        }

        scopes.AddRange(resourceScopes);

        var validated = supportedScopes is { Count: > 0 };
        var unsupported = validated
            ? vendorUnsupported
                .Concat(resourceScopes.Where(scope => !IsSupported(scope, supportedScopes!)))
                .ToList()
            : vendorUnsupported;

        return new GeneratedScopesDto(
            ScopeVersion: version,
            ScopeVersionDetected: scopeVersionDetected,
            Scopes: scopes,
            ScopeString: string.Join(' ', scopes),
            UnsupportedScopes: unsupported,
            // A vendor profile is itself a statement of what the server publishes, captured from its own
            // discovery document — so a profile-filtered result is validated even without a live scopes_supported.
            ValidatedAgainstDiscovery: validated || profile is not null);
    }

    // A scope is supported if the server advertises it exactly, or via a wildcard that covers it — e.g. advertised
    // "user/*.rs" or "user/*.*" covers "user/Patient.rs". Comparison is case-insensitive on both resource and action.
    private static bool IsSupported(string scope, IReadOnlyCollection<string> supported)
    {
        foreach (var advertised in supported)
        {
            if (string.Equals(advertised, scope, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (WildcardCovers(advertised, scope))
            {
                return true;
            }
        }

        return false;
    }

    // Matches an advertised scope with '*' wildcards in the resource and/or action segment against a concrete scope.
    // Both must share the same prefix and be of the form {prefix}/{resource}.{action}.
    private static bool WildcardCovers(string advertised, string scope)
    {
        var (aPrefix, aResource, aAction) = SplitScope(advertised);
        var (sPrefix, sResource, sAction) = SplitScope(scope);
        if (aResource is null || sResource is null)
        {
            return false;
        }

        return string.Equals(aPrefix, sPrefix, StringComparison.OrdinalIgnoreCase)
            && (aResource == "*" || string.Equals(aResource, sResource, StringComparison.OrdinalIgnoreCase))
            && (aAction == "*" || string.Equals(aAction, sAction, StringComparison.OrdinalIgnoreCase));
    }

    private static (string Prefix, string? Resource, string Action) SplitScope(string scope)
    {
        var slash = scope.IndexOf('/');
        if (slash < 0)
        {
            return (scope, null, string.Empty);
        }

        var prefix = scope[..slash];
        var rest = scope[(slash + 1)..];
        var dot = rest.LastIndexOf('.');
        return dot < 0 ? (prefix, rest, string.Empty) : (prefix, rest[..dot], rest[(dot + 1)..]);
    }
}
