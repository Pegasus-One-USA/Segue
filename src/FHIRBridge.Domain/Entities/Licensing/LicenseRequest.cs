using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Licensing;

/// <summary>Lifecycle of one install's outbound license request.</summary>
public enum LicenseRequestStatus
{
    /// <summary>Created, but the outbound call to the licensor's intake endpoint hasn't been attempted
    /// (or is in flight) yet.</summary>
    Pending,

    /// <summary>The direct API call to the licensor's intake endpoint succeeded.</summary>
    Submitted,

    /// <summary>The direct API call failed (network/DNS/non-2xx) — the operator now shares
    /// <see cref="LicenseRequest.EncodedPayload"/> with the licensor manually instead.</summary>
    Failed,
}

/// <summary>
/// This install's own outbound request for a license, submitted once and then reused for every later
/// renewal — never a new row per request. <see cref="UniqueKey"/> is generated exactly once, at creation,
/// and never changes; it is embedded as the <c>requestKey</c> claim in the license eventually minted
/// against this request, and <c>LicenseService.ApplyAsync</c> refuses to apply a license whose
/// <c>requestKey</c> claim doesn't match this row's key — proving the license being activated was minted
/// for THIS install's request, not one copied from a different customer.
///
/// Contact details can be corrected later via <see cref="UpdateDetails"/> (e.g. a typo'd email) without
/// ever touching <see cref="UniqueKey"/> — that's the one thing that must never change, since it's what
/// keeps identifying the same install/customer across its entire license lifetime, embedded as the
/// <c>requestKey</c> claim in whatever gets minted against it.
/// </summary>
public sealed class LicenseRequest : Entity<Guid>
{
    private LicenseRequest()
    {
    }

    public LicenseRequest(
        Guid id, string clientName, string email, string? companyName, string? address, string phoneNumber,
        string uniqueKey, DateTime createdUtc, string? requestHost = null)
    {
        Id = id;
        ClientName = clientName;
        Email = email;
        CompanyName = companyName;
        Address = address;
        PhoneNumber = phoneNumber;
        UniqueKey = uniqueKey;
        Status = LicenseRequestStatus.Pending;
        CreatedUtc = createdUtc;
        RequestHost = requestHost;
    }

    public string ClientName { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public string? CompanyName { get; private set; }
    public string? Address { get; private set; }
    public string PhoneNumber { get; private set; } = default!;

    /// <summary>Generated once at creation, never regenerated — see this class's remarks.</summary>
    public string UniqueKey { get; private set; } = default!;

    /// <summary>The admin's own browser origin at submit time (e.g. "https://fhirbridge.acmehealth.com" or
    /// "http://localhost:4200"), captured client-side since the API's own Host header only reflects where
    /// the API itself is bound, never the portal's port — lets the licensor tell which deployment a
    /// request came from. Refreshed on every <see cref="Resubmit"/>, same as the other contact fields via
    /// <see cref="UpdateDetails"/> — unlike <see cref="UniqueKey"/>, this isn't an identity anchor, just the
    /// most recently observed origin, so a later resend correcting a wrong earlier value is expected.</summary>
    public string? RequestHost { get; private set; }

    public LicenseRequestStatus Status { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public DateTime? LastAttemptUtc { get; private set; }

    /// <summary>Set only when <see cref="Status"/> is <see cref="LicenseRequestStatus.Failed"/> — a short,
    /// operator-facing reason (e.g. "DNS resolution failed", "503 from licensor"), never PHI/secret.</summary>
    public string? SubmissionError { get; private set; }

    public void MarkSubmitted(DateTime attemptedUtc)
    {
        Status = LicenseRequestStatus.Submitted;
        LastAttemptUtc = attemptedUtc;
        SubmissionError = null;
    }

    public void MarkFailed(DateTime attemptedUtc, string error)
    {
        Status = LicenseRequestStatus.Failed;
        LastAttemptUtc = attemptedUtc;
        SubmissionError = error;
    }

    /// <summary>Resets status to Pending ahead of a fresh outbound attempt — used internally by
    /// <see cref="UpdateDetails"/>'s caller after a correction, so the licensor's copy picks up the edit
    /// too. <paramref name="requestHost"/> refreshes <see cref="RequestHost"/> with the current browser
    /// origin when supplied; a null/blank value (a non-browser caller) leaves the existing one alone
    /// rather than clearing it.</summary>
    public void Resubmit(string? requestHost = null)
    {
        Status = LicenseRequestStatus.Pending;
        if (!string.IsNullOrWhiteSpace(requestHost))
        {
            RequestHost = requestHost.Trim();
        }
    }

    /// <summary>Corrects the contact details this install originally submitted — e.g. a typo'd email or a
    /// changed phone number — before or after the licensor has looked at it. Never touches
    /// <see cref="UniqueKey"/>. Doesn't itself re-attempt the outbound call; the caller follows up with
    /// <see cref="Resubmit"/> so the licensor's copy picks up the correction too.</summary>
    public void UpdateDetails(
        string clientName, string email, string? companyName, string? address, string phoneNumber)
    {
        ClientName = clientName;
        Email = email;
        CompanyName = companyName;
        Address = address;
        PhoneNumber = phoneNumber;
    }
}
