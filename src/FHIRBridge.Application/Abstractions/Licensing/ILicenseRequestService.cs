namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>Fields collected on the blank first-time request form. Never re-collected on a renewal — see
/// <see cref="ILicenseRequestService.ResubmitAsync"/>.</summary>
public sealed record LicenseRequestInput(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber);

/// <summary>Current state of this install's one-and-only license request, for the portal to render —
/// covers "no request yet" (<see cref="Exists"/> false), Pending/Submitted/Failed, and (only when Failed)
/// the manual-fallback blob to share with the licensor.</summary>
public sealed record LicenseRequestStatusResult(
    bool Exists,
    string? ClientName,
    string? Email,
    string? CompanyName,
    string? Address,
    string? PhoneNumber,
    string? Status,
    DateTime? CreatedUtc,
    DateTime? LastAttemptUtc,
    string? SubmissionError,
    /// <summary>Populated only when <see cref="Status"/> is "Failed" — the same payload the direct API call
    /// would have sent, encoded as one copy-pasteable string for the operator to share with the licensor
    /// manually (email/support ticket) instead.</summary>
    string? EncodedPayload)
{
    public static LicenseRequestStatusResult NotRequested { get; } =
        new(false, null, null, null, null, null, null, null, null, null, null);
}

/// <summary>
/// This install's outbound license request — one row, ever (see <c>LicenseRequest</c>'s own remarks).
/// Generates a <c>UniqueKey</c> at first request, tries the licensor's direct intake API, and falls back to
/// an encoded manual-share blob when that call doesn't succeed. A renewal (<see cref="ResubmitAsync"/>)
/// reuses the exact same stored details and key — an admin cannot change contact details on a renewal,
/// since the whole point of <c>UniqueKey</c> is that it keeps identifying the same install across its
/// entire license lifetime.
/// </summary>
public interface ILicenseRequestService
{
    Task<LicenseRequestStatusResult> GetAsync(CancellationToken cancellationToken);

    /// <summary>Fails with <see cref="InvalidOperationException"/> if a request already exists for this
    /// install — use <see cref="ResubmitAsync"/> for a renewal instead.</summary>
    Task<LicenseRequestStatusResult> CreateAndSubmitAsync(LicenseRequestInput input, CancellationToken cancellationToken);

    /// <summary>Fails with <see cref="InvalidOperationException"/> if no request exists yet for this
    /// install — use <see cref="CreateAndSubmitAsync"/> first.</summary>
    Task<LicenseRequestStatusResult> ResubmitAsync(CancellationToken cancellationToken);
}
