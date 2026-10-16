using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Immutable record of one CDS Hooks call (invoke or feedback) — the CDS Activity screen. Holds call metadata
/// only: never the request body, context, prefetch, fhirAuthorization, patient or user ids, or card text.
/// <see cref="HookInstance"/> is the EHR-generated UUID for the hook firing, not a clinical identifier.
/// </summary>
public sealed class CdsCallLog : Entity<Guid>, IAppendOnlyEntity
{
    public const int ServiceIdMaxLength = 100;
    public const int HandlerKeyMaxLength = 100;
    public const int HookMaxLength = 100;
    public const int HookInstanceMaxLength = 100;
    public const int IssuerMaxLength = 500;
    public const int DetailMaxLength = 500;
    public const int TriggerActionMaxLength = 20;
    public const int VersionMaxLength = 50;
    public const int CriteriaIdMaxLength = 100;
    public const int OverrideReasonCodesMaxLength = 300;

    private CdsCallLog()
    {
    }

    public CdsCallLog(
        DateTime occurredOnUtc,
        CdsCallKind kind,
        string serviceId,
        Guid? serviceInstanceId,
        string? handlerKey,
        string? hook,
        string? hookInstance,
        string? issuer,
        Guid? trustedIssuerId,
        CdsCallOutcome outcome,
        int httpStatus,
        int cardCount,
        long durationMs,
        string? detail,
        CdsCallLogExtras? extras = null)
    {
        Id = Guid.NewGuid();
        OccurredOnUtc = occurredOnUtc;
        Kind = kind;
        ServiceId = Truncate(serviceId, ServiceIdMaxLength) ?? string.Empty;
        ServiceInstanceId = serviceInstanceId;
        HandlerKey = Truncate(handlerKey, HandlerKeyMaxLength);
        Hook = Truncate(hook, HookMaxLength);
        HookInstance = Truncate(hookInstance, HookInstanceMaxLength);
        Issuer = Truncate(issuer, IssuerMaxLength);
        TrustedIssuerId = trustedIssuerId;
        Outcome = outcome;
        HttpStatus = httpStatus;
        CardCount = cardCount;
        DurationMs = durationMs;
        Detail = Truncate(detail, DetailMaxLength);

        extras ??= CdsCallLogExtras.None;
        TriggerAction = Truncate(extras.TriggerAction, TriggerActionMaxLength);
        EpicVersion = Truncate(extras.EpicVersion, VersionMaxLength);
        CriteriaId = Truncate(extras.CriteriaId, CriteriaIdMaxLength);
        ImplementationVersion = Truncate(extras.ImplementationVersion, VersionMaxLength);
        CallbackResourceCount = extras.CallbackResourceCount;
        CallbackMs = extras.CallbackMs;
        SuggestionsOffered = Math.Max(extras.SuggestionsOffered, 0);
        SuggestionsAccepted = Math.Max(extras.SuggestionsAccepted, 0);
        OverrideReasonCodes = Truncate(extras.OverrideReasonCodes, OverrideReasonCodesMaxLength);
    }

    public DateTime OccurredOnUtc { get; private set; }
    public CdsCallKind Kind { get; private set; }

    /// <summary>The service id from the URL, as called (it may match no instance).</summary>
    public string ServiceId { get; private set; } = default!;

    public Guid? ServiceInstanceId { get; private set; }
    public string? HandlerKey { get; private set; }
    public string? Hook { get; private set; }
    public string? HookInstance { get; private set; }

    /// <summary>The JWT's <c>iss</c>, when one could be read.</summary>
    public string? Issuer { get; private set; }

    public Guid? TrustedIssuerId { get; private set; }
    public CdsCallOutcome Outcome { get; private set; }
    public int HttpStatus { get; private set; }

    /// <summary>Cards returned (invoke) or feedback items received (feedback).</summary>
    public int CardCount { get; private set; }

    public long DurationMs { get; private set; }

    /// <summary>A short, non-PHI reason (e.g. "Missing prefetch: allergies", "Signature did not verify").</summary>
    public string? Detail { get; private set; }

    /// <summary>Epic request extension <c>com.epic.cdshooks.request.bpa-trigger-action</c> (e.g. "5", "60"). Invoke only.</summary>
    public string? TriggerAction { get; private set; }

    /// <summary>Epic request extension <c>com.epic.cdshooks.request.epic-version</c>.</summary>
    public string? EpicVersion { get; private set; }

    /// <summary>Epic request extension <c>com.epic.cdshooks.request.criteria-id</c> (the Epic CDS rule record id).</summary>
    public string? CriteriaId { get; private set; }

    /// <summary>Epic request extension <c>com.epic.cdshooks.request.cds-hooks-implementation-version</c>.</summary>
    public string? ImplementationVersion { get; private set; }

    /// <summary>Resources fetched from the client's fhirServer for missing prefetch; null when no callback was made.</summary>
    public int? CallbackResourceCount { get; private set; }

    /// <summary>Time spent on the FHIR callback; null when no callback was made.</summary>
    public long? CallbackMs { get; private set; }

    /// <summary>Invoke: suggestions (one-click fixes) offered across all cards. Feedback: 0.</summary>
    public int SuggestionsOffered { get; private set; }

    /// <summary>Feedback: suggestions the clinician accepted (acceptedSuggestions ids). Invoke: 0.</summary>
    public int SuggestionsAccepted { get; private set; }

    /// <summary>
    /// Feedback: the distinct override reason codes received, comma-separated (codes only — Epic reasonCategory
    /// codes included, never a userComment or display text).
    /// </summary>
    public string? OverrideReasonCodes { get; private set; }

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length > maxLength ? value[..maxLength] : value;
}

/// <summary>Optional metadata added to a <see cref="CdsCallLog"/> row (all non-PHI, short values).</summary>
public sealed record CdsCallLogExtras(
    string? TriggerAction = null,
    string? EpicVersion = null,
    string? CriteriaId = null,
    string? ImplementationVersion = null,
    int? CallbackResourceCount = null,
    long? CallbackMs = null,
    int SuggestionsOffered = 0,
    int SuggestionsAccepted = 0,
    string? OverrideReasonCodes = null)
{
    public static CdsCallLogExtras None { get; } = new();
}
