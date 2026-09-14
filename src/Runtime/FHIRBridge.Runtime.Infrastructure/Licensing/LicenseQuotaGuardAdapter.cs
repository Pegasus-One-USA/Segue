using FHIRBridge.Runtime.Application.Abstractions.Pipeline;

namespace FHIRBridge.Runtime.Infrastructure.Licensing;

/// <summary>
/// Thin forwarding adapter onto the product's real license quota guard. Exists purely to satisfy
/// <see cref="IPipelineRunLicenseGuard"/> (defined in Runtime.Application, which cannot reference the main
/// FHIRBridge.Application project) — this project already references FHIRBridge.Application, so it is the
/// correct place to bridge the two.
/// </summary>
public sealed class LicenseQuotaGuardAdapter : IPipelineRunLicenseGuard
{
    private readonly FHIRBridge.Application.Abstractions.Licensing.ILicenseQuotaGuard _licenseQuotaGuard;

    public LicenseQuotaGuardAdapter(FHIRBridge.Application.Abstractions.Licensing.ILicenseQuotaGuard licenseQuotaGuard)
    {
        _licenseQuotaGuard = licenseQuotaGuard;
    }

    public Task EnsureCanStartNewRunAsync(CancellationToken cancellationToken) =>
        _licenseQuotaGuard.EnsureCanStartNewRunAsync(cancellationToken);

    public Task EnsureWorkflowQuotaAvailableAsync(CancellationToken cancellationToken) =>
        _licenseQuotaGuard.EnsureWorkflowQuotaAvailableAsync(cancellationToken);
}
