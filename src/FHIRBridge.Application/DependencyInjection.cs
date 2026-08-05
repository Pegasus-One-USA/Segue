using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Services;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddFHIRBridgeApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<ConfigurationService>();
        services.AddScoped<IJsonMappingEngine, JsonMappingEngine>();
        services.AddSingleton<IParentReferenceResolver, ParentReferenceResolver>();

        // Vendor-keyed catalogs (registry-over-switch: one class, one instance per catalog file, picked
        // by key rather than a switch on SourceSystemType — see MappingController.GetCatalogFields).
        // "Generic" is also exposed unkeyed so every pre-existing plain `IFhirElementCatalog` injection
        // (SourceCapabilitiesController, etc.) keeps resolving to the same base-FHIR-R4 catalog instance
        // it always has, unchanged.
        services.AddKeyedSingleton<IFhirElementCatalog>(
            FhirElementCatalogKeys.Generic, (_, _) => new EmbeddedFhirElementCatalog("fhir-r4-catalog.json"));
        services.AddKeyedSingleton<IFhirElementCatalog>(
            FhirElementCatalogKeys.Epic, (_, _) => new EmbeddedFhirElementCatalog("fhir-r4-catalog.epic.json"));
        services.AddSingleton<IFhirElementCatalog>(sp =>
            sp.GetRequiredKeyedService<IFhirElementCatalog>(FhirElementCatalogKeys.Generic));
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
        services.AddScoped<IBulkExportPollService, BulkExportPollService>();

        return services;
    }
}
