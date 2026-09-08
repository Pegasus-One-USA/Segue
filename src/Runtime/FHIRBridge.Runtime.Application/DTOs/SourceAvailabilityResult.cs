namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>
/// Whether a source's FHIR endpoint is answering at all. Deliberately three-valued: the third state is what keeps
/// a preflight check from ever blocking a run it merely failed to measure.
/// </summary>
public enum SourceAvailability
{
    /// <summary>
    /// Something at the other end answered. Note this includes 401/403/404 — a server that says "unauthorized" or
    /// "not found" has demonstrably received and processed the request, so it is up and any subsequent auth failure
    /// is a real credentials/registration problem rather than an outage. Treating those as "down" would be the
    /// single easiest way to break working connections.
    /// </summary>
    Up,

    /// <summary>
    /// Nothing usable answered: DNS failure, connection refused, TLS failure, timeout, an open circuit breaker, or
    /// a gateway-class status (502/503/504) that means the front door is up but has nothing behind it.
    /// </summary>
    Down,

    /// <summary>
    /// The probe itself could not reach a verdict. Callers MUST treat this as permission to proceed — see
    /// <c>ISourceAvailabilityProbe</c>.
    /// </summary>
    Unknown,
}

/// <summary>
/// The verdict plus a short, PHI-free explanation of how it was reached, so a failure message can quote the actual
/// observation ("connection refused", "HTTP 503") rather than asserting an unexplained "source is down".
/// </summary>
/// <param name="Availability">The verdict.</param>
/// <param name="Detail">
/// Author-written or transport-derived text safe for logs and for inclusion in a client-facing sentence. Never an
/// upstream response body — only a status number or a socket-level reason.
/// </param>
public sealed record SourceAvailabilityResult(SourceAvailability Availability, string Detail)
{
    public bool IsDown => Availability == SourceAvailability.Down;

    public static SourceAvailabilityResult Up(string detail) => new(SourceAvailability.Up, detail);

    public static SourceAvailabilityResult Down(string detail) => new(SourceAvailability.Down, detail);

    public static SourceAvailabilityResult Unknown(string detail) => new(SourceAvailability.Unknown, detail);
}
