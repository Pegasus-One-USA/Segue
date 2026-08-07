using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Mapping;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Application.Services.Transforms.Nodes;
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
        services.AddScoped<IMappingImportService, MappingImportService>();
        services.AddScoped<ISchemaMatchingService, SchemaMatchingService>();

        // The 20 field-level transform nodes (FHIRBridge_Top20_Transformations.pdf) — stateless, registered as
        // singletons and resolved by type via ITransformNodeRegistry (registry-over-switch: a 21st node means
        // one more AddSingleton<ITransformNode, X>() line here, never a switch anywhere else).
        services.AddSingleton<ITransformNode, DateTimeFormatNode>();
        services.AddSingleton<ITransformNode, NumberCastNode>();
        services.AddSingleton<ITransformNode, BooleanConversionNode>();
        services.AddSingleton<ITransformNode, UnitConversionNode>();
        services.AddSingleton<ITransformNode, QuantityRangeAssemblyNode>();
        services.AddSingleton<ITransformNode, RoundingScalingNode>();
        services.AddSingleton<ITransformNode, ValueCodeMappingNode>();
        services.AddSingleton<ITransformNode, CodeableConceptBuilderNode>();
        services.AddSingleton<ITransformNode, StatusEnumCoercionNode>();
        services.AddSingleton<ITransformNode, ReferenceConstructionNode>();
        services.AddSingleton<ITransformNode, IdentifierFormattingNode>();
        services.AddSingleton<ITransformNode, HumanNameParsingNode>();
        services.AddSingleton<ITransformNode, AddressParsingNode>();
        services.AddSingleton<ITransformNode, TelecomNormalizationNode>();
        services.AddSingleton<ITransformNode, StringNormalizationNode>();
        services.AddSingleton<ITransformNode, ConcatenationTemplatingNode>();
        services.AddSingleton<ITransformNode, ArrayListOperationsNode>();
        services.AddSingleton<ITransformNode, DefaultNullHandlingNode>();
        services.AddSingleton<ITransformNode, DateMathAgeNode>();
        services.AddSingleton<ITransformNode, HashingMaskingNode>();
        services.AddSingleton<ITransformNodeRegistry, TransformNodeRegistry>();
        services.AddScoped<IEffectiveRuleResolver, EffectiveRuleResolver>();
        services.AddScoped<ITransformationRuleService, TransformationRuleService>();
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
        services.AddScoped<IMappingImportService, MappingImportService>();

        return services;
    }
}
