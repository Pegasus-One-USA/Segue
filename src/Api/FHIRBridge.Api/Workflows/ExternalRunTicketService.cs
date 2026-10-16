using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

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
    string TicketId,
    string? GroupId = null,
    string? SearchCriteria = null,
    string? ResourceCriteria = null);

/// <summary>
/// Short-lived, single-use launch tickets for the browser-redirect trigger flow. The caller's BACKEND proves its
/// identity with the Client ID/Secret (server-to-server) and receives a ticket; only the ticket travels through the
/// browser. The ticket is a Data Protection time-limited payload (tamper-proof and self-expiring) plus a one-time
/// marker so a captured link cannot be replayed.
/// <para>
/// Reading a ticket (<see cref="Read"/>) and using it up (<see cref="TryClaimAsync"/>) are separate steps: the caller
/// validates the run first and claims the ticket only right before starting it, so a ticket isn't burned by a run that
/// never starts (license, workflow or endpoint refusal). The claim is atomic — a Redis SET NX when Redis is configured,
/// a process-wide lock over the in-memory cache otherwise — so two simultaneous redemptions (a link scanner plus the
/// user, a prefetch) can't both start the run.
/// </para>
/// </summary>
public sealed class ExternalRunTicketService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(90);

    private const string Purpose = "FHIRBridge.ExternalRunTicket.v1";

    private static readonly SemaphoreSlim InProcessClaimLock = new(1, 1);

    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDistributedCache _cache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<ExternalRunTicketService> _logger;

    public ExternalRunTicketService(
        IDataProtectionProvider dataProtectionProvider,
        IDistributedCache cache,
        ILogger<ExternalRunTicketService> logger,
        IConnectionMultiplexer? redis = null)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _cache = cache;
        _logger = logger;
        _redis = redis;
    }

    public string Issue(ExternalRunTicket ticket)
    {
        var withId = ticket with { TicketId = Guid.NewGuid().ToString("N") };
        return _dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(withId), Lifetime);
    }

    /// <summary>Returns the ticket, or null when it is malformed, expired or tampered with. Does NOT use it up —
    /// see <see cref="TryClaimAsync"/>.</summary>
    public ExternalRunTicket? Read(string protectedTicket)
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

        return ticket is null || string.IsNullOrEmpty(ticket.TicketId) ? null : ticket;
    }

    /// <summary>
    /// Uses the ticket up, atomically: true for exactly one caller, false when it was already used. Fails closed — a
    /// cache that can't be reached returns false (the caller reports invalid_ticket) rather than throwing a 500.
    /// </summary>
    public async Task<bool> TryClaimAsync(ExternalRunTicket ticket, CancellationToken cancellationToken)
    {
        var key = $"external-run-ticket:{ticket.TicketId}";
        try
        {
            if (_redis is not null)
            {
                return await _redis.GetDatabase().StringSetAsync(
                    $"fhirbridge:{key}", 1, Lifetime * 2, When.NotExists);
            }

            // No Redis means the in-memory distributed cache, which is per process anyway — a process-wide lock makes
            // its read-then-write atomic.
            await InProcessClaimLock.WaitAsync(cancellationToken);
            try
            {
                if (await _cache.GetAsync(key, cancellationToken) is not null)
                {
                    return false;
                }

                await _cache.SetAsync(key, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime * 2 }, cancellationToken);
                return true;
            }
            finally
            {
                InProcessClaimLock.Release();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not record use of external run ticket; treating it as already used.");
            return false;
        }
    }
}
