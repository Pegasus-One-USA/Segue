namespace FHIRBridge.Domain.Enums;

/// <summary>How a CDS Hooks call ended, as recorded on its <see cref="Entities.Governance.CdsCallLog"/> row.</summary>
public enum CdsCallOutcome
{
    /// <summary>Answered with one or more cards (or, for feedback, accepted).</summary>
    Success = 1,

    /// <summary>Answered normally, but the rules produced no cards.</summary>
    NoCards = 2,

    /// <summary>Answered with no cards because data the handler needed was missing (spec 2.0 graceful degradation).</summary>
    Degraded = 3,

    /// <summary>412: required prefetch missing and no fhirServer to fetch it from.</summary>
    PreconditionFailed = 4,

    /// <summary>401: missing, invalid or untrusted JWT.</summary>
    Unauthorized = 5,

    /// <summary>403: a trusted issuer not on the instance's allow-list.</summary>
    Forbidden = 6,

    /// <summary>404: no enabled instance with that service id.</summary>
    NotFound = 7,

    /// <summary>400: malformed request, or a hook the instance does not answer.</summary>
    BadRequest = 8,

    /// <summary>500: the handler failed.</summary>
    Error = 9,
}
