using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace FHIRBridge.Api.Workflows;

/// <summary>
/// Carries the Group ID / Search Criteria an external caller supplied when it triggered a workflow, across the hops that
/// cannot be trusted with plain values: the browser is redirected to the portal's /external-run page, out to the EHR's
/// sign-in, back, and only then calls POST /run.
/// <para>The token is encrypted and signed (data protection), expires, and names the one workflow it is for, so the page
/// can hand it back to <c>/run</c> as proof that an authenticated trigger - a client id + secret, or a launch ticket -
/// chose these exact values. Without it, per-run overrides are reserved for signed-in portal users and are refused for
/// the anonymous /external-run page.</para>
/// </summary>
public static class ExternalRunOverridesToken
{
    public const string ProtectorPurpose = "FHIRBridge.ExternalRunOverrides.v1";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private sealed record Payload(Guid WorkflowId, string? GroupId, string? SearchCriteria);

    /// <summary>Null when the caller supplied neither value (nothing to carry).</summary>
    public static string? Protect(IDataProtectionProvider provider, Guid workflowId, string? groupId, string? searchCriteria)
    {
        if (groupId is null && searchCriteria is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(new Payload(workflowId, groupId, searchCriteria));
        return provider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector().Protect(json, Lifetime);
    }

    /// <summary>False for a tampered, expired, or another workflow's token.</summary>
    public static bool TryRead(
        IDataProtectionProvider provider, string token, Guid workflowId, out string? groupId, out string? searchCriteria)
    {
        groupId = null;
        searchCriteria = null;
        try
        {
            var json = provider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector().Unprotect(token);
            var payload = JsonSerializer.Deserialize<Payload>(json);
            if (payload is null || payload.WorkflowId != workflowId)
            {
                return false;
            }

            groupId = payload.GroupId;
            searchCriteria = payload.SearchCriteria;
            return true;
        }
        catch (Exception)
        {
            // Tampered, expired, wrong key ring, or not a token at all: all the same answer.
            return false;
        }
    }
}
