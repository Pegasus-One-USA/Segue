namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>SystemSetting keys the license-request flow reads/writes, shared between
/// <c>LicenseRequestService</c> (Infrastructure) and <c>LicenseRequestController</c> (Api) so both sides
/// name the exact same key. Kept in the Application layer since both of those are DI clients of it, not
/// owners — same reasoning as any other cross-layer constant.</summary>
public static class LicenseRequestSettingKeys
{
    /// <summary>The licensor's base URL — see <c>LicenseRequestController.GetLicensorUrl</c>/
    /// <c>SetLicensorUrl</c> for the narrow, license-gate-allowlisted endpoint pair that exposes just this
    /// one key, instead of the general-purpose (and NOT allowlisted) SystemSettingsController.</summary>
    public const string LicensorApplicationUrl = "License:LicensorApplicationUrl";
}

/// <summary>Fields collected on the request form — for a brand new submission or to correct an existing
/// one via <see cref="ILicenseRequestService.UpdateAsync"/>.</summary>
public sealed record LicenseRequestInput(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber);

/// <summary>One submitted license request, for the portal to render — covers Pending/Submitted/Failed, and
/// (only when Failed) the manual-fallback blob to share with the licensor. This install can have any number
/// of these; each is independent, identified by <see cref="Id"/>.</summary>
public sealed record LicenseRequestStatusResult(
    Guid Id,
    string ClientName,
    string Email,
    string? CompanyName,
    string? Address,
    string PhoneNumber,
    string Status,
    DateTime CreatedUtc,
    DateTime? LastAttemptUtc,
    string? SubmissionError,
    /// <summary>Populated only when <see cref="Status"/> is "Failed" — the same payload the direct API call
    /// would have sent, encoded as one copy-pasteable string for the operator to share with the licensor
    /// manually (email/support ticket) instead.</summary>
    string? EncodedPayload,
    /// <summary>The admin's own browser origin at the most recent submission of this request — see
    /// <c>LicenseRequest.RequestHost</c>'s remarks.</summary>
    string? RequestHost);

/// <summary>
/// This install's outbound license requests — any number of them, each independent (see
/// <c>LicenseRequest</c>'s own remarks). Every "Submit Request" creates a new one, with its own
/// <c>UniqueKey</c>, and tries the licensor's direct intake API, falling back to an encoded manual-share
/// blob when that call doesn't succeed.
/// </summary>
public interface ILicenseRequestService
{
    Task<IReadOnlyList<LicenseRequestStatusResult>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Always creates a brand-new request with a freshly generated key — never fails because one
    /// already exists. <paramref name="requestHost"/> is the admin's own browser origin at submit time
    /// (falling back to the inbound HTTP request's Host header only if that wasn't supplied) — see
    /// <c>LicenseRequestController.ResolveRequestedFrom</c>. Purely informational for the licensor, never
    /// used for any security decision.</summary>
    Task<LicenseRequestStatusResult> CreateAndSubmitAsync(
        LicenseRequestInput input, string? requestHost, CancellationToken cancellationToken);

    /// <summary>Corrects an existing request's contact details (e.g. a typo'd email) and re-attempts the
    /// outbound call so the licensor's copy is corrected too — never touches <c>UniqueKey</c>. Fails with
    /// <see cref="InvalidOperationException"/> if <paramref name="id"/> doesn't match one of this install's
    /// own requests.</summary>
    Task<LicenseRequestStatusResult> UpdateAsync(
        Guid id, LicenseRequestInput input, string? requestHost, CancellationToken cancellationToken);
}
