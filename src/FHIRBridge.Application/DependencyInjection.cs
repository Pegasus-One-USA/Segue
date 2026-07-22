using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddFHIRBridgeApplication(this IServiceCollection services)
    {
        services.AddScoped<IJsonMappingEngine, JsonMappingEngine>();
        services.AddSingleton<IFhirElementCatalog, EmbeddedFhirElementCatalog>();
        services.AddSingleton<IParentReferenceResolver, ParentReferenceResolver>();
        services.AddScoped<IMappingMaterializer, DefaultMappingMaterializer>();
        services.AddScoped<IResourceNormalizationService, PassThroughResourceNormalizationService>();
        services.AddScoped<IMappedRecordNormalizationService, PassThroughMappedRecordNormalizationService>();
        services.AddScoped<IGovernancePolicyService, DefaultGovernancePolicyService>();
        services.AddScoped<IDeIdentificationService, PassThroughDeIdentificationService>();
        services.AddScoped<IRetentionPolicyService, DefaultRetentionPolicyService>();
        services.AddScoped<IConfigurationService, ConfigurationService>();
        services.AddScoped<IEhrEndpointService, EhrEndpointService>();
        services.AddScoped<IAllowedCorsOriginsService, AllowedCorsOriginsService>();
        services.AddSingleton<IScopeGeneratorService, ScopeGeneratorService>();
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<ILocalAuthService, LocalAuthService>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<ISetupService, SetupService>();
        services.AddScoped<ISsoAuthService, SsoAuthService>();
        services.AddScoped<IRoleManagementService, RoleManagementService>();
        services.AddScoped<IHedisMeasureReportService, HedisMeasureReportService>();
        services.AddScoped<IAnomalyDetectionService, RunAnomalyDetectionService>();
        services.AddScoped<IPipelineRunMetricsService, PipelineRunMetricsService>();

        return services;
    }
}
