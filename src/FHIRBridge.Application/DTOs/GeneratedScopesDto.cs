namespace FHIRBridge.Application.DTOs;

/// <summary>
/// The SMART scope set generated for a source, plus provenance. <see cref="Scopes"/> is the ordered scope list and
/// <see cref="ScopeString"/> the space-joined form sent to the authorization server. When the source's advertised
/// <c>scopes_supported</c> was available, <see cref="ValidatedAgainstDiscovery"/> is true and
/// <see cref="UnsupportedScopes"/> lists any generated scope the server does not appear to support.
/// </summary>
public sealed record GeneratedScopesDto(
    string ScopeVersion,
    bool ScopeVersionDetected,
    IReadOnlyList<string> Scopes,
    string ScopeString,
    IReadOnlyList<string> UnsupportedScopes,
    bool ValidatedAgainstDiscovery);
