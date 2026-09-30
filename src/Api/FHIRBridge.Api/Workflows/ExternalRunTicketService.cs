using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace FHIRBridge.Api.Workflows;

/// <summary>What a launch ticket carries: everything /workflows/external/run-page needs, already validated when the
/// ticket was minted (credential, workflow, allowed caller URL) so the browser never has to hold the Client Secret.</summary>
public sealed record ExternalRunTicket(
    Guid ApiClientId,
    string ClientId,
    Guid WorkflowId,
    string? ReturnUrl,
    string? EhrEndpointCode,
    string? Mode,
    string? WindowMode,
    string? CloseOnComplete,
    string TicketId);

/// <summary>
/// Short-lived, single-use launch tickets for the browser-redirect trigger flow. The caller's BACKEND proves its
/// identity with the Client ID/Secret (server-to-server) and receives a ticket; only the ticket travels through the
/// browser. The ticket is a Data Protection time-limited payload (tamper-proof and self-expiring) plus a one-time
/// marker in the distributed cache so a captured link cannot be replayed.
/// </summary>
public sealed class ExternalRunTicketService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(90);

    private const string Purpose = "FHIRBridge.ExternalRunTicket.v1";

    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDistributedCache _cache;

    public ExternalRunTicketService(IDataProtectionProvider dataProtectionProvider, IDistributedCache cache)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _cache = cache;
    }

    public string Issue(ExternalRunTicket ticket)
    {
        var withId = ticket with { TicketId = Guid.NewGuid().ToString("N") };
        return _dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(withId), Lifetime);
    }

    /// <summary>Returns the ticket and burns it, or null when it is malformed, expired, tampered with or already used.</summary>
    public async Task<ExternalRunTicket?> RedeemAsync(string protectedTicket, CancellationToken cancellationToken)
    {
        ExternalRunTicket? ticket;
        try
        {
            var json = _dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(protectedTicket);
            ticket = JsonSerializer.Deserialize<ExternalRunTicket>(json);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }

        if (ticket is null || string.IsNullOrEmpty(ticket.TicketId))
        {
            return null;
        }

        var key = $"external-run-ticket:{ticket.TicketId}";
        if (await _cache.GetAsync(key, cancellationToken) is not null)
        {
            return null;
        }

        await _cache.SetAsync(key, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime * 2 }, cancellationToken);
        return ticket;
    }
}
