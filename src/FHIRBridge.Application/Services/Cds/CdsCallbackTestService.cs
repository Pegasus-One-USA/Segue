using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services.Cds;

/// <summary>
/// Admin "Test callback" for a trusted issuer: GET {EffectiveFhirBaseUrl}/metadata with no token and no patient
/// data, to check that Segue can reach the EHR's FHIR server before a live call needs it. Runs even when the
/// issuer's callbacks are off (the message says so), since checking reachability is harmless.
/// </summary>
public sealed class CdsCallbackTestService : ICdsCallbackTestService
{
    private readonly ICdsTrustedIssuerRepository _issuers;
    private readonly ICdsFhirCallbackClient _callbackClient;

    public CdsCallbackTestService(ICdsTrustedIssuerRepository issuers, ICdsFhirCallbackClient callbackClient)
    {
        _issuers = issuers;
        _callbackClient = callbackClient;
    }

    public async Task<CdsCallbackTestResultDto> TestAsync(Guid trustedIssuerId, CancellationToken cancellationToken)
    {
        var issuer = await _issuers.GetByIdAsync(trustedIssuerId, cancellationToken)
            ?? throw new NotFoundException("CdsTrustedIssuer", trustedIssuerId);
        var fhirBaseUrl = issuer.EffectiveFhirBaseUrl;
        var probe = await _callbackClient.ProbeMetadataAsync(fhirBaseUrl, cancellationToken);

        string message;
        if (probe.Reachable)
        {
            var server = string.Join(", ", new[]
            {
                probe.Software,
                probe.FhirVersion is null ? null : $"FHIR {probe.FhirVersion}",
            }.Where(part => part is not null));
            message = $"The FHIR server answered in {probe.DurationMs} ms" +
                      (server.Length > 0 ? $" ({server})." : ".") +
                      (issuer.AllowCallback
                          ? " Segue can fetch missing data from it."
                          : " Callbacks are off for this issuer, so Segue will not fetch missing data from it.");
        }
        else
        {
            message = $"The FHIR server could not be used: {probe.FailureReason ?? "unknown reason."}";
        }

        return new CdsCallbackTestResultDto(
            issuer.Id,
            fhirBaseUrl,
            issuer.AllowCallback,
            probe.Reachable,
            probe.HttpStatus,
            probe.FhirVersion,
            probe.Software,
            probe.DurationMs,
            message);
    }
}
