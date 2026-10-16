using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Which parts of an update change the write-side settings of an EHR write connection, and so need the EHR Write-Back
/// edit right on top of the vendor's own edit right. A connection that cannot write has no write side, so nothing here
/// applies (turning write access on is checked separately, against the requested Access).
/// </summary>
public static class SourceConnectionWriteSide
{
    /// <summary>
    /// True when <paramref name="request"/> would change a write-side setting of <paramref name="saved"/>, a
    /// connection that can write today: its Access (including dropping write, which also clears activation and the
    /// department), its department, or its vendor write-API activation (switching it off sends every write back to
    /// dry run). Only the Destination Connections form sends these; a source form sends null for all three, which
    /// keeps the saved values, so a re-save from Source Connections or a canvas source node needs the right only when
    /// it also repoints the connection (below).
    ///
    /// <para><b>Where the writes go.</b> A write connection's endpoint and credentials decide which EHR receives live
    /// writes and the PHI in them, so changing its vendor, base URL, token endpoint, client id, key id, JWKS URL,
    /// practice, secret references or the secret itself is a write-side change too. A value the request leaves out
    /// (null or blank: a form that keeps the saved secret, for instance) keeps the saved one and is no change; scopes
    /// and the token request's auth placement are not where the writes go. A JWKS URL filled in where none was saved is
    /// no change either: the source forms back-fill the derived default on connections saved before it was kept.</para>
    /// </summary>
    public static bool UpdateChangesWriteSide(SourceConnectionDto saved, CreateSourceConnectionRequest request)
    {
        if (!saved.Access.AllowsWrite())
        {
            return false;
        }

        return (request.Access is { } access && access != saved.Access)
               || (request.DepartmentId is { } departmentId && !SameText(departmentId, saved.DepartmentId))
               || (request.VendorWriteApisActivated is { } activated && activated != saved.VendorWriteApisActivated)
               || RepointsTarget(saved, request);
    }

    private static bool RepointsTarget(SourceConnectionDto saved, CreateSourceConnectionRequest request)
    {
        var sent = request.Authentication;
        var kept = saved.Authentication;
        return request.SourceSystemType != saved.SourceSystemType
               || !SameUrl(request.BaseUrl, saved.BaseUrl)
               || (sent is not null
                   && (Changed(sent.TokenEndpoint, kept.TokenEndpoint, SameUrl)
                       || Changed(sent.ClientId, kept.ClientId, SameText)
                       || Changed(sent.KeyId, kept.KeyId, SameText)
                       || (!string.IsNullOrWhiteSpace(kept.JwksUrl) && Changed(sent.JwksUrl, kept.JwksUrl, SameUrl))
                       || Changed(sent.PracticeId, kept.PracticeId, SameText)
                       || Changed(sent.ClientSecretKeyVaultName, kept.ClientSecretKeyVaultName, SameText)
                       || Changed(sent.ClientSecretName, kept.ClientSecretName, SameText)
                       || Changed(sent.PrivateKeyKeyVaultName, kept.PrivateKeyKeyVaultName, SameText)
                       || Changed(sent.PrivateKeySecretName, kept.PrivateKeySecretName, SameText)
                       || !string.IsNullOrWhiteSpace(sent.InlineClientSecret)));
    }

    /// <summary>A value the request sends that differs from the saved one; a value it leaves out keeps the saved one.</summary>
    private static bool Changed(string? sent, string? saved, Func<string?, string?, bool> same) =>
        !string.IsNullOrWhiteSpace(sent) && !same(sent, saved);

    private static bool SameUrl(string? left, string? right) =>
        string.Equals(Normalize(left)?.TrimEnd('/'), Normalize(right)?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool SameText(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
