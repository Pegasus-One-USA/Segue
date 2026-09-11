using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;
using Microsoft.AspNetCore.DataProtection;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// ASP.NET Core Data Protection implementation of <see cref="ILaunchTokenProtector"/>. Uses authenticated encryption
/// (tamper-evident) with separate purposes for the launch-context and OAuth-state tokens, so a token minted for one
/// purpose cannot be replayed as the other. The protected output is URL-safe base64, so it drops straight into a URL
/// path segment or query value.
/// </summary>
public sealed class DataProtectionLaunchTokenProtector : ILaunchTokenProtector
{
    private readonly IDataProtector _contextProtector;
    private readonly IDataProtector _stateProtector;

    public DataProtectionLaunchTokenProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _contextProtector = dataProtectionProvider.CreateProtector("FHIRBridge.OAuthLaunch.Context.v1");
        _stateProtector = dataProtectionProvider.CreateProtector("FHIRBridge.OAuthLaunch.State.v1");
    }

    // An optional hospital/organization EhrEndpoint id rides as a "|eh:{guid:N}" suffix on either token shape below.
    // Appending it (rather than a new discriminator) keeps every previously minted token — which never has this
    // suffix — parsing exactly as before.
    private const string EhrEndpointSuffixPrefix = "|eh:";

    // An optional caller-supplied callerId (the URL to redirect to once OAuth completes) rides as a
    // "|cid:{base64url}" suffix, applied BEFORE the "|eh:" suffix (i.e. it ends up as the inner suffix, "|eh:" stays
    // the true outermost marker). Base64url output never contains "|", so SplitEhrEndpointSuffix's IndexOf keeps
    // finding the real "|eh:" marker unambiguously, and a token with neither suffix (every token minted before this
    // feature) still parses exactly as before.
    private const string CallerIdSuffixPrefix = "|cid:";

    // An optional caller-supplied sessionId (see LaunchContext.SessionId) rides as a "|sid:{base64url}" suffix,
    // applied BEFORE the "|cid:" suffix so a token minted before this feature — or one with only "|cid:"/"|eh:" —
    // still parses exactly as before.
    private const string SessionIdSuffixPrefix = "|sid:";

    // An optional caller-supplied userIdentity (see LaunchContext.UserIdentity) rides as a "|uid:{base64url}"
    // suffix, applied BEFORE the "|sid:" suffix (innermost of the four) so a token minted before this feature — or
    // one with only "|sid:"/"|cid:"/"|eh:" — still parses exactly as before.
    private const string UserIdentitySuffixPrefix = "|uid:";

    // The attempt-scoped correlation id minted by validate-run (see LaunchContext.CorrelationId) rides as a
    // "|rid:{base64url}" suffix, applied INNERMOST of the five so a token minted before this feature — or one
    // carrying only the earlier suffixes — still parses byte-for-byte as it did before. This is the only way the
    // id can reach /oauth/callback: that leg arrives as a redirect from the EHR, which carries no headers of ours.
    private const string CorrelationIdSuffixPrefix = "|rid:";

    public string ProtectContext(Guid routeId, Guid? ehrEndpointId = null, string? callerId = null, string? sessionId = null, string? userIdentity = null, string? correlationId = null) =>
        _contextProtector.Protect(
            AppendEhrEndpointSuffix(
                AppendCallerIdSuffix(
                    AppendSessionIdSuffix(
                        AppendUserIdentitySuffix(
                            AppendCorrelationIdSuffix($"{routeId:N}", correlationId), userIdentity), sessionId), callerId), ehrEndpointId));

    // Workflow launch tokens carry a "wf:" discriminator so UnprotectContext can tell a workflow launch from a route
    // launch. Route tokens stay the bare "{guid:N}" form (backward compatible with previously minted launch URLs).
    public string ProtectWorkflowContext(Guid workflowId, Guid? ehrEndpointId = null, string? callerId = null, string? sessionId = null, string? userIdentity = null, string? correlationId = null) =>
        _contextProtector.Protect(
            AppendEhrEndpointSuffix(
                AppendCallerIdSuffix(
                    AppendSessionIdSuffix(
                        AppendUserIdentitySuffix(
                            AppendCorrelationIdSuffix($"wf:{workflowId:N}", correlationId), userIdentity), sessionId), callerId), ehrEndpointId));

    private static string AppendEhrEndpointSuffix(string value, Guid? ehrEndpointId) =>
        ehrEndpointId is { } id ? $"{value}{EhrEndpointSuffixPrefix}{id:N}" : value;

    private static string AppendCallerIdSuffix(string value, string? callerId) =>
        string.IsNullOrEmpty(callerId) ? value : $"{value}{CallerIdSuffixPrefix}{Base64UrlEncode(callerId)}";

    private static string AppendSessionIdSuffix(string value, string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? value : $"{value}{SessionIdSuffixPrefix}{Base64UrlEncode(sessionId)}";

    private static string AppendCorrelationIdSuffix(string value, string? correlationId) =>
        string.IsNullOrEmpty(correlationId) ? value : $"{value}{CorrelationIdSuffixPrefix}{Base64UrlEncode(correlationId)}";

    private static string AppendUserIdentitySuffix(string value, string? userIdentity) =>
        string.IsNullOrEmpty(userIdentity) ? value : $"{value}{UserIdentitySuffixPrefix}{Base64UrlEncode(userIdentity)}";

    // Splits a trailing "|eh:{guid:N}" suffix off a decrypted value, returning the value with the suffix removed
    // plus the parsed EhrEndpoint id (null if there was no suffix, or it didn't parse as a guid).
    private static (string Value, Guid? EhrEndpointId) SplitEhrEndpointSuffix(string value)
    {
        var separatorIndex = value.IndexOf(EhrEndpointSuffixPrefix, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return (value, null);
        }

        var head = value[..separatorIndex];
        var suffix = value[(separatorIndex + EhrEndpointSuffixPrefix.Length)..];
        return Guid.TryParseExact(suffix, "N", out var ehrEndpointId) ? (head, ehrEndpointId) : (head, null);
    }

    // Splits a trailing "|cid:{base64url}" suffix off a decrypted value (with any "|eh:" suffix already removed),
    // returning the value with the suffix removed plus the decoded callerId (null if there was no suffix, or it
    // didn't decode as valid base64url).
    private static (string Value, string? CallerId) SplitCallerIdSuffix(string value)
    {
        var separatorIndex = value.IndexOf(CallerIdSuffixPrefix, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return (value, null);
        }

        var head = value[..separatorIndex];
        var suffix = value[(separatorIndex + CallerIdSuffixPrefix.Length)..];
        return TryBase64UrlDecode(suffix, out var callerId) ? (head, callerId) : (head, null);
    }

    // Splits a trailing "|sid:{base64url}" suffix off a decrypted value (with any "|eh:"/"|cid:" suffixes already
    // removed), returning the value with the suffix removed plus the decoded sessionId (null if there was no
    // suffix, or it didn't decode as valid base64url).
    private static (string Value, string? SessionId) SplitSessionIdSuffix(string value)
    {
        var separatorIndex = value.IndexOf(SessionIdSuffixPrefix, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return (value, null);
        }

        var head = value[..separatorIndex];
        var suffix = value[(separatorIndex + SessionIdSuffixPrefix.Length)..];
        return TryBase64UrlDecode(suffix, out var sessionId) ? (head, sessionId) : (head, null);
    }

    // Splits a trailing "|uid:{base64url}" suffix off a decrypted value (with any "|eh:"/"|cid:"/"|sid:" suffixes
    // already removed), returning the value with the suffix removed plus the decoded userIdentity (null if there
    // was no suffix, or it didn't decode as valid base64url).
    // Splits a trailing "|rid:{base64url}" suffix off a decrypted value (with every other suffix already removed).
    private static (string Value, string? CorrelationId) SplitCorrelationIdSuffix(string value)
    {
        var separatorIndex = value.IndexOf(CorrelationIdSuffixPrefix, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return (value, null);
        }

        var head = value[..separatorIndex];
        var suffix = value[(separatorIndex + CorrelationIdSuffixPrefix.Length)..];
        return TryBase64UrlDecode(suffix, out var correlationId) ? (head, correlationId) : (head, null);
    }

    private static (string Value, string? UserIdentity) SplitUserIdentitySuffix(string value)
    {
        var separatorIndex = value.IndexOf(UserIdentitySuffixPrefix, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return (value, null);
        }

        var head = value[..separatorIndex];
        var suffix = value[(separatorIndex + UserIdentitySuffixPrefix.Length)..];
        return TryBase64UrlDecode(suffix, out var userIdentity) ? (head, userIdentity) : (head, null);
    }

    private static string Base64UrlEncode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out string decoded)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return true;
        }
        catch (FormatException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    // Checkpoint tokens carry a third "chk:" discriminator plus both ids, pipe-delimited. Distinct prefix from "wf:"
    // so a checkpoint token can never be replayed as a full-workflow launch token or vice versa.
    public string ProtectWorkflowCheckpointContext(Guid workflowId, Guid targetNodeId) =>
        _contextProtector.Protect($"chk:{workflowId:N}|{targetNodeId:N}");

    public LaunchContext? UnprotectContext(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var value = _contextProtector.Unprotect(token);

            if (value.StartsWith("chk:", StringComparison.Ordinal))
            {
                var parts = value["chk:".Length..].Split('|');
                if (parts.Length == 2
                    && Guid.TryParseExact(parts[0], "N", out var checkpointWorkflowId)
                    && Guid.TryParseExact(parts[1], "N", out var targetNodeId))
                {
                    return new LaunchContext(RouteId: null, WorkflowId: checkpointWorkflowId, TargetNodeId: targetNodeId);
                }

                return null;
            }

            if (value.StartsWith("wf:", StringComparison.Ordinal))
            {
                var (wfHead, wfEhrEndpointId) = SplitEhrEndpointSuffix(value["wf:".Length..]);
                var (wfCidHead, wfCallerId) = SplitCallerIdSuffix(wfHead);
                var (wfSidHead, wfSessionId) = SplitSessionIdSuffix(wfCidHead);
                var (wfUidHead, wfUserIdentity) = SplitUserIdentitySuffix(wfSidHead);
                var (wfValue, wfCorrelationId) = SplitCorrelationIdSuffix(wfUidHead);
                if (Guid.TryParseExact(wfValue, "N", out var workflowId))
                {
                    return new LaunchContext(
                        RouteId: null, WorkflowId: workflowId, EhrEndpointId: wfEhrEndpointId, CallerId: wfCallerId,
                        SessionId: wfSessionId, UserIdentity: wfUserIdentity, CorrelationId: wfCorrelationId);
                }

                return null;
            }

            var (routeHead, routeEhrEndpointId) = SplitEhrEndpointSuffix(value);
            var (routeCidHead, routeCallerId) = SplitCallerIdSuffix(routeHead);
            var (routeSidHead, routeSessionId) = SplitSessionIdSuffix(routeCidHead);
            var (routeUidHead, routeUserIdentity) = SplitUserIdentitySuffix(routeSidHead);
            var (routeValue, routeCorrelationId) = SplitCorrelationIdSuffix(routeUidHead);
            if (Guid.TryParseExact(routeValue, "N", out var routeId))
            {
                return new LaunchContext(
                    RouteId: routeId, EhrEndpointId: routeEhrEndpointId, CallerId: routeCallerId, SessionId: routeSessionId,
                    UserIdentity: routeUserIdentity, CorrelationId: routeCorrelationId);
            }

            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public string ProtectState(string nonce) => _stateProtector.Protect(nonce);

    public string? UnprotectState(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return _stateProtector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
