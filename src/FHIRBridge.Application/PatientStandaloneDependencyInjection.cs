using FHIRBridge.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Application;

/// <summary>
/// DI registration for Patient Standalone's own services, kept out of <see cref="DependencyInjection"/> so that
/// file — which the Provider Standalone flow's registrations also live in — never needs to change for this feature.
/// Call <see cref="AddPatientStandaloneApplicationServices"/> alongside <c>AddFHIRBridgeApplication()</c> in the API
/// host's composition root.
/// </summary>
public static class PatientStandaloneDependencyInjection
{
    public static IServiceCollection AddPatientStandaloneApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IPatientStandaloneEhrEndpointService, PatientStandaloneEhrEndpointService>();

        return services;
    }
}
