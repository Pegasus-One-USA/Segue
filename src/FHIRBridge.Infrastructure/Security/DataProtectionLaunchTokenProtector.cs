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

    public string ProtectContext(Guid routeId, Guid? ehrEndpointId = null) =>
        _contextProtector.Protect(AppendEhrEndpointSuffix($"{routeId:N}", ehrEndpointId));

    // Workflow launch tokens carry a "wf:" discriminator so UnprotectContext can tell a workflow launch from a route
    // launch. Route tokens stay the bare "{guid:N}" form (backward compatible with previously minted launch URLs).
    public string ProtectWorkflowContext(Guid workflowId, Guid? ehrEndpointId = null) =>
        _contextProtector.Protect(AppendEhrEndpointSuffix($"wf:{workflowId:N}", ehrEndpointId));

    private static string AppendEhrEndpointSuffix(string value, Guid? ehrEndpointId) =>
        ehrEndpointId is { } id ? $"{value}{EhrEndpointSuffixPrefix}{id:N}" : value;

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
                var (wfValue, wfEhrEndpointId) = SplitEhrEndpointSuffix(value["wf:".Length..]);
                if (Guid.TryParseExact(wfValue, "N", out var workflowId))
                {
                    return new LaunchContext(RouteId: null, WorkflowId: workflowId, EhrEndpointId: wfEhrEndpointId);
                }

                return null;
            }

            var (routeValue, routeEhrEndpointId) = SplitEhrEndpointSuffix(value);
            if (Guid.TryParseExact(routeValue, "N", out var routeId))
            {
                return new LaunchContext(RouteId: routeId, EhrEndpointId: routeEhrEndpointId);
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
