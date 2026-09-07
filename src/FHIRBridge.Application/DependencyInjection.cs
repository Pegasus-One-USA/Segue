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
        // Scoped, not Singleton, like the other 19 — it optionally consumes the Scoped ITerminologyLookupService
        // (real DB access per Icd10/Snomed/RxNorm/Loinc terminology tables) to resolve a real display for a bare
        // code. ITransformNodeRegistry below must stay Scoped too, or it would capture this as a captive
        // dependency the first time it's constructed and hold a stale scope for the app's lifetime.
        services.AddScoped<ITransformNode, CodeableConceptBuilderNode>();
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
        // Scoped (not Singleton) because it now composes one Scoped node (CodeableConceptBuilderNode) alongside
        // the 19 Singleton ones — see the comment above. A Scoped registry resolving a mix of Scoped/Singleton
        // dependencies is fine; a Singleton registry capturing a Scoped one is the captive-dependency bug this
        // avoids. Both of its current callers (TransformationRuleService, MappingNodeExecutor) are Scoped already.
        services.AddScoped<ITransformNodeRegistry, TransformNodeRegistry>();
        services.AddScoped<IEffectiveRuleResolver, EffectiveRuleResolver>();
        // FHIR-native counterparts: same node registry, keyed on a FHIR path instead of a destination
        // column. Only reachable from V2's FhirResourceTransformNode.
        services.AddScoped<IFhirResourceRuleResolver, FhirResourceRuleResolver>();
        services.AddScoped<IFhirResourceTransformService, FhirResourceTransformService>();
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
