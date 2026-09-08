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
        SourceSystemType? vendor = null,
        bool isGroupExport = false)
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
            // Policy exclusion, applied ahead of every tier below because it must hold no matter what the server
            // advertises. eCW DOES publish system/Group.read, but its Backend Authentication guide requires that
            // scope excluded from Backend Single Patient calls — the only eCW backend flow FHIRBridge supports —
            // and present only for Group $export/bulk. VendorScopeCatalog omits it for that reason, but the
            // discovery tier below reads the live document, which advertises it: without this the discovery tier
            // would hand it straight back and break single-patient auth. Reported, so the omission stays visible
            // rather than silently differing from what the operator selected.
            if (IsPolicyExcluded(prefix, resourceType, vendor))
            {
                vendorUnsupported.Add($"{prefix}/{resourceType}.{suffix}");
                continue;
            }

            // Tier 1 — what THIS server advertises right now, read straight off its own scopes_supported. Strictly
            // better than the captured profile below: VendorScopeCatalog's eCW map was itself derived from one
            // practice's discovery document on one day, and eCW tenants differ, so a snapshot can only go stale.
            // Deriving per connection also makes the v1-only rule fall out rather than needing to be known — a
            // server that publishes no '.rs' anywhere can never have one generated for it.
            var discoveredAccessLevel = prefix == "system" && supportedScopes is { Count: > 0 }
                ? FindAdvertisedReadAccessLevel(prefix, resourceType, supportedScopes)
                : null;
            if (discoveredAccessLevel is not null)
            {
                resourceScopes.Add($"{prefix}/{resourceType}.{discoveredAccessLevel}");
            }
            // Tier 2 — the vendor's captured profile, for when discovery is unreachable or silent on this type.
            else if (profile is not null && profile.TryGetReadAccessLevel(resourceType, out var vendorAccessLevel))
            {
                resourceScopes.Add($"{prefix}/{resourceType}.{vendorAccessLevel}");
            }
            // A profiled vendor that publishes nothing for this type contributes no scope at all — reported, never
            // requested. Only a profile is authoritative enough to say "this type has no read scope here"; absence
            // from a possibly-unreachable discovery document is not, so unprofiled vendors fall through to Tier 3.
            else if (profile is not null)
            {
                vendorUnsupported.Add($"{prefix}/{resourceType}.{suffix}");
            }
            // Tier 3 — the uniform version suffix, unchanged for every vendor with no profile (Epic included).
            else
            {
                resourceScopes.Add($"{prefix}/{resourceType}.{suffix}");
            }
        }

        scopes.AddRange(resourceScopes);

        // Group $export (bulk) needs the Group read scope on top of the per-resource scopes above — eCW's Backend
        // Authentication guide mandates system/Group.read for a Group/{id}/$export, and SMART bulk requires it
        // generally. Group is deliberately kept OUT of the per-resource set (IsPolicyExcluded / VendorScopeCatalog
        // omit it) so it can never leak into a Backend Single Patient grant, which eCW requires it excluded from —
        // so this flag is the only path that requests it. Added only for a system/ (Backend) group export.
        if (isGroupExport && prefix == "system")
        {
            var groupScope = $"{prefix}/Group.{suffix}";
            if (!scopes.Contains(groupScope, StringComparer.OrdinalIgnoreCase))
            {
                scopes.Add(groupScope);
            }
        }

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

    /// <summary>
    /// Scopes a vendor must never be asked for even when it advertises them — vendor policy rather than vendor
    /// vocabulary, which is why no discovery document can tell us and it cannot live in
    /// <see cref="VendorScopeCatalog"/>'s "what is published" map alone. Currently one rule: eCW's
    /// <c>system/Group.read</c>, which its own guide requires excluded from Backend Single Patient calls.
    /// </summary>
    private static bool IsPolicyExcluded(string prefix, string resourceType, SourceSystemType? vendor) =>
        prefix == "system"
        && vendor == SourceSystemType.Healow
        && string.Equals(resourceType, "Group", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The access level this server actually advertises for one resource type's read scope, or null when it
    /// advertises none. Read levels only — a write level ('.c'/'.u'/'.d'/'.w') is never a substitute, since
    /// FHIRBridge reads and asking for write access it doesn't need is both wrong and a compliance smell.
    /// <para>
    /// Preference order is widest-read-first among the spellings that mean "read": 'read' (SMARTv1 coarse),
    /// then 'rs' (v2 read+search), then bare 'r'. Only one is requested — asking for two spellings of the same
    /// permission is what a vendor that validates its vocabulary strictly rejects.
    /// </para>
    /// A literal wildcard entry ('system/*.read') is deliberately NOT treated as advertising a concrete type:
    /// it says nothing about which types exist, and expanding it per type is how a request ends up naming a type
    /// the server doesn't actually serve.
    /// </summary>
    private static string? FindAdvertisedReadAccessLevel(
        string prefix,
        string resourceType,
        IReadOnlyCollection<string> supportedScopes)
    {
        var advertised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in supportedScopes)
        {
            var (scopePrefix, scopeResource, scopeAction) = SplitScope(scope);
            if (scopeResource is null
                || !string.Equals(scopePrefix, prefix, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(scopeResource, resourceType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            advertised.Add(scopeAction);
        }

        foreach (var candidate in ReadAccessLevelPreference)
        {
            if (advertised.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static readonly string[] ReadAccessLevelPreference = ["read", "rs", "r"];

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
