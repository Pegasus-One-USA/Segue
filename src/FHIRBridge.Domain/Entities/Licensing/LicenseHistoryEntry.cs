using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Licensing;

/// <summary>
/// Immutable audit-trail snapshot of one <c>LicenseService.ApplyAsync</c> call that actually succeeded.
/// <c>LicenseService</c>'s own "current" token (SystemSetting "License:Token") always reflects only the
/// MOST RECENT successful apply — this table keeps every prior one too, purely for an admin to look back
/// at "what did we apply, and when" without that history being overwritten each time a new license is
/// activated.
/// </summary>
public sealed class LicenseHistoryEntry : Entity<Guid>
{
    private LicenseHistoryEntry()
    {
    }

    public LicenseHistoryEntry(
        Guid id, string token, DateTime appliedUtc, string? customerName, string? edition, string state,
        DateTime? expiresUtc)
    {
        Id = id;
        Token = token;
        AppliedUtc = appliedUtc;
        CustomerName = customerName;
        Edition = edition;
        State = state;
        ExpiresUtc = expiresUtc;
    }

    /// <summary>The raw signed token as applied — not a secret (it's the same artifact the licensor handed
    /// the customer), kept so a previously-applied license can be inspected without re-requesting it.</summary>
    public string Token { get; private set; } = default!;

    public DateTime AppliedUtc { get; private set; }
    public string? CustomerName { get; private set; }
    public string? Edition { get; private set; }

    /// <summary>The <c>LicenseState</c> this token resolved to AT THE TIME it was applied (e.g. "Active") —
    /// not re-evaluated later, since this row is a point-in-time snapshot, not a live status.</summary>
    public string State { get; private set; } = default!;

    public DateTime? ExpiresUtc { get; private set; }
}
