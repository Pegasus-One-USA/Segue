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

    public string ProtectContext(Guid routeId) =>
        _contextProtector.Protect($"{routeId:N}");

    // Workflow launch tokens carry a "wf:" discriminator so UnprotectContext can tell a workflow launch from a route
    // launch. Route tokens stay the bare "{guid:N}" form (backward compatible with previously minted launch URLs).
    public string ProtectWorkflowContext(Guid workflowId) =>
        _contextProtector.Protect($"wf:{workflowId:N}");

    public LaunchContext? UnprotectContext(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var value = _contextProtector.Unprotect(token);

            if (value.StartsWith("wf:", StringComparison.Ordinal)
                && Guid.TryParseExact(value["wf:".Length..], "N", out var workflowId))
            {
                return new LaunchContext(RouteId: null, WorkflowId: workflowId);
            }

            if (Guid.TryParseExact(value, "N", out var routeId))
            {
                return new LaunchContext(RouteId: routeId);
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
