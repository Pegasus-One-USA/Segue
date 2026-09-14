namespace FHIRBridge.SharedKernel.Exceptions;

/// <summary>
/// Thrown for every license rejection that is NOT a counted-quota cap (see
/// <see cref="LicenseQuotaExceededException"/> for those): an allow-list miss (source vendor type, hospital
/// endpoint, FHIR resource type, or destination type not named on the license), a truly expired license
/// blocking a new pipeline run, or the monthly processed-records cap being at/over its limit. These are
/// deliberately kept as one exception type with a <see cref="Reason"/> discriminator rather than a small
/// hierarchy — there are only a handful of shapes here and none of them need their own behavior, only their
/// own message.
/// </summary>
public sealed class LicenseRestrictionViolationException : FHIRBridgeException
{
    public LicenseRestrictionViolationException(string reason, string userMessage)
        : base($"License restriction violation ({reason}): {userMessage}", userMessage)
    {
        Reason = reason;
    }

    /// <summary>Short machine-readable discriminator, e.g. "SourceTypeNotAllowed", "HospitalNotAllowed",
    /// "ResourceTypeNotAllowed", "DestinationTypeNotAllowed", "LicenseExpired", "ProcessedRecordsQuotaExceeded".</summary>
    public string Reason { get; }
}
