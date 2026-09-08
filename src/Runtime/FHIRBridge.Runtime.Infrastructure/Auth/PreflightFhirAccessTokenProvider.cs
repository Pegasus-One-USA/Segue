using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Wraps token acquisition with a source-availability check, so a run against a source that is DOWN says so instead
/// of being reported as a credentials problem.
/// <para>
/// It decorates <see cref="CompositeFhirAccessTokenProvider"/> — by that class's own remarks, the single dispatch
/// point every vendor and grant type funnels through — so one registration covers all four application types
/// (Backend Services, EHR launch, standalone, patient) without any branching on
/// <c>ApplicationType</c> and therefore without touching the no-switch architecture rule.
/// </para>
/// <para>
/// Two checks, and the second is the important one:
/// </para>
/// <list type="number">
/// <item><b>Before</b> requesting a token: if the source is positively Down, fail immediately rather than spending
/// the full outbound retry/timeout budget against a dead host and tripping the circuit breaker on the way.</item>
/// <item><b>After</b> a token request fails: re-check availability and, if the source has gone Down, re-word the
/// failure as the outage it is. This is what actually fixes the reported symptom — a vendor whose edge answers
/// normally (so check 1 passes) while the service behind it is degraded and returns a bare <c>invalid_client</c>.
/// </item>
/// </list>
/// <para>
/// <b>Fail open.</b> Only a positive Down verdict blocks; <see cref="SourceAvailability.Unknown"/> always proceeds.
/// A preflight that guesses wrong does not merely print the wrong sentence — it cancels work that would have
/// succeeded — so the real call stays the final judge.
/// </para>
/// <para>
/// <see cref="IFhirPatientContextProvider"/> and <see cref="IFhirGrantedScopeProvider"/> are implemented as
/// straight pass-throughs because callers feature-detect the registered <see cref="IFhirAccessTokenProvider"/> by
/// pattern-matching on them (<c>SourceConnectionRuntimeResolver</c> for an interactive launch's resolved base URL,
/// <c>FhirSourceConnectorBase</c> for patient context, <c>SourceNodeExecutors</c> for granted scopes). A decorator
/// that implemented only the token interface would silently disable all three.
/// </para>
/// </summary>
public sealed class PreflightFhirAccessTokenProvider
    : IFhirAccessTokenProvider, IFhirPatientContextProvider, IFhirGrantedScopeProvider
{
    private readonly IFhirAccessTokenProvider _inner;
    private readonly ISourceAvailabilityProbe _availabilityProbe;
    private readonly ILogger _logger;

    /// <param name="inner">
    /// The provider to wrap — in composition this is <see cref="CompositeFhirAccessTokenProvider"/>, resolved
    /// explicitly by concrete type in DI so registering this decorator as the <see cref="IFhirAccessTokenProvider"/>
    /// creates no resolution cycle. Typed as the abstraction rather than the concrete class so the decorator's own
    /// before/after logic is testable against a fake.
    /// </param>
    public PreflightFhirAccessTokenProvider(
        IFhirAccessTokenProvider inner,
        ISourceAvailabilityProbe availabilityProbe,
        ILogger<PreflightFhirAccessTokenProvider>? logger = null)
    {
        _inner = inner;
        _availabilityProbe = availabilityProbe;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public async Task<string> GetAccessTokenAsync(
        FhirSourceConfiguration source,
        CancellationToken cancellationToken)
    {
        // source.BaseUrl is already the RESOLVED base URL by the time a run reaches here — SourceConnectionRuntimeResolver
        // applies an interactive launch's EhrEndpoint override before building this configuration — so the probe
        // reports on the host this run will actually call, not the connection's configured default.
        // FhirSourceConfiguration.Name is nullable; a connection saved without one still deserves a readable
        // sentence rather than "Source '' is not reachable".
        var sourceName = string.IsNullOrWhiteSpace(source.Name) ? "this source" : source.Name;

        var preflight = await _availabilityProbe.CheckAsync(source.BaseUrl, cancellationToken);
        if (preflight.IsDown)
        {
            _logger.LogWarning(
                "Skipping token acquisition for source {SourceName}: the FHIR endpoint is not reachable. {Detail}",
                sourceName,
                preflight.Detail);

            throw new SourceUnavailableException(sourceName, preflight.Detail);
        }

        try
        {
            return await _inner.GetAccessTokenAsync(source, cancellationToken);
        }
        catch (Exception exception) when (ShouldRecheckAvailability(exception))
        {
            var recheck = await _availabilityProbe.CheckAsync(source.BaseUrl, cancellationToken);
            if (!recheck.IsDown)
            {
                // The source is genuinely up and genuinely refused us — the original diagnosis stands.
                throw;
            }

            _logger.LogWarning(
                exception,
                "Token acquisition for source {SourceName} failed while its FHIR endpoint is unreachable — " +
                "reporting an outage rather than a credentials failure. {Detail}",
                sourceName,
                recheck.Detail);

            throw new SourceUnavailableException(sourceName, recheck.Detail, exception);
        }
    }

    // Pass-throughs. Each falls back to the same "not applicable" answer the strategy base uses when the wrapped
    // provider doesn't implement the interface, so wrapping a token-only provider degrades exactly as an
    // undecorated one would rather than throwing.

    public Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _inner is IFhirPatientContextProvider patientContext
            ? patientContext.GetPatientContextAsync(source, cancellationToken)
            : Task.FromResult<string?>(null);

    public Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _inner is IFhirPatientContextProvider patientContext
            ? patientContext.GetResolvedBaseUrlAsync(source, cancellationToken)
            : Task.FromResult<string?>(null);

    public Task DiscardTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _inner is IFhirPatientContextProvider patientContext
            ? patientContext.DiscardTokenAsync(source, cancellationToken)
            : Task.CompletedTask;

    public Task<string?> GetGrantedScopeAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _inner is IFhirGrantedScopeProvider grantedScope
            ? grantedScope.GetGrantedScopeAsync(source, cancellationToken)
            : Task.FromResult<string?>(null);

    /// <summary>
    /// Which token failures earn a re-probe: everything except cancellation (so a cancelled run is never
    /// relabelled as an outage) and <see cref="SourceUnavailableException"/> (so the before/after checks can't
    /// stack on each other).
    /// <para>
    /// Deliberately broad — in particular it INCLUDES a plain 400 <c>invalid_client</c>, because that is precisely
    /// the reported case: a vendor edge that answers normally while its own client-registry/JWKS lookup fails
    /// behind the scenes, producing a credentials-shaped error during what is really an outage. Narrowing this to
    /// 5xx-only would leave the original bug in place.
    /// </para>
    /// </summary>
    private static bool ShouldRecheckAvailability(Exception exception) =>
        exception is not OperationCanceledException and not SourceUnavailableException;
}
