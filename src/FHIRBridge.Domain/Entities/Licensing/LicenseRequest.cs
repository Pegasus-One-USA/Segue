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
/// Contact details are fixed at creation time (see <see cref="Resubmit"/>): a renewal resends the exact
/// same details rather than letting an admin change them, since the whole point of <see cref="UniqueKey"/>
/// is that it keeps identifying the same install/customer across its entire license lifetime.
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

    /// <summary>The domain and, when non-default, port this install's API was reached on when the request
    /// was created (the inbound HTTP request's Host header, e.g. "fhirbridge.acmehealth.com" or
    /// "localhost:5000") — lets the licensor tell which deployment a request came from. Captured once at
    /// creation, never re-derived on <see cref="Resubmit"/> (a renewal always runs on the same install).
    /// Null if the request predates this field or the Host header was somehow absent.</summary>
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

    /// <summary>Called on a renewal attempt — re-tries the outbound call with the SAME stored details and
    /// <see cref="UniqueKey"/>, never accepting new contact details from the caller.</summary>
    public void Resubmit() => Status = LicenseRequestStatus.Pending;
}
