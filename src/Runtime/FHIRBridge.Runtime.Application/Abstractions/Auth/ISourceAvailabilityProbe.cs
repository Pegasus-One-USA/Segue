using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

/// <summary>
/// Answers one question about a source: is its FHIR endpoint answering right now?
/// <para>
/// This is the only component in the system that knows the difference between "the vendor said no" and "the vendor
/// said nothing" — a distinction nothing could previously make, which is why an Epic outage was reported to
/// operators as a credentials problem. It is used twice by <c>PreflightFhirAccessTokenProvider</c>: once before a
/// token is requested (to fail a run fast instead of burning the full retry/timeout budget against a dead host)
/// and once after a token request fails (to decide whether the failure should be worded as an outage).
/// </para>
/// <para>
/// The probe targets the FHIR base URL's <c>/metadata</c>, which is unauthenticated per the SMART App Launch spec
/// — so there is no chicken-and-egg with the token it is trying to protect.
/// </para>
/// <para>
/// <b>Contract:</b> this must never throw, and must never return <see cref="SourceAvailability.Down"/> on a doubt.
/// A wrong "down" verdict does not merely produce a bad message — it cancels work that would have succeeded — so
/// anything the probe cannot positively establish is <see cref="SourceAvailability.Unknown"/>, which every caller
/// treats as permission to proceed and let the real call be the judge.
/// </para>
/// </summary>
public interface ISourceAvailabilityProbe
{
    /// <param name="baseUrl">
    /// The FHIR base URL to check. Callers pass the RESOLVED base URL (an interactive launch can override the
    /// connection's configured one), so the probe reports on the host the run is actually going to use.
    /// </param>
    Task<SourceAvailabilityResult> CheckAsync(string? baseUrl, CancellationToken cancellationToken);
}
