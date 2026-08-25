using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs.Transforms;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

public sealed class TransformationRuleService : ITransformationRuleService
{
    private readonly ITransformationRuleRepository _repository;
    private readonly IEffectiveRuleResolver _resolver;
    private readonly ITransformNodeRegistry _nodeRegistry;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IAppSecretAccessor? _secretAccessor;

    public TransformationRuleService(
        ITransformationRuleRepository repository,
        IEffectiveRuleResolver resolver,
        ITransformNodeRegistry nodeRegistry,
        IConfigurationRepository configurationRepository,
        IAppSecretAccessor? secretAccessor = null)
    {
        _repository = repository;
        _resolver = resolver;
        _nodeRegistry = nodeRegistry;
        _configurationRepository = configurationRepository;
        _secretAccessor = secretAccessor;
    }

    public async Task<List<TransformationRuleDto>> ListRulesAsync(
        TransformScope? scope,
        DestinationType? destinationType,
        string? resourceType,
        string? destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem = null,
        string? sourceField = null,
        CancellationToken cancellationToken = default)
    {
        var rules = await _repository.ListAsync(
            scope, destinationType, resourceType, destinationField, resourcePipelineRouteId, sourceSystem, sourceField, cancellationToken);
        return rules.Select(ToDto).ToList();
    }

    public async Task<TransformationRuleDto> SaveRuleAsync(
        SaveTransformationRuleRequest request, CancellationToken cancellationToken = default)
    {
        var configJson = JsonSerializer.Serialize(request.Config);
        var existing = request.Id is null ? null : await _repository.GetByIdAsync(request.Id.Value, cancellationToken);

        if (existing is null)
        {
            var rule = new TransformationRule(
                request.Scope,
                request.NodeType,
                configJson,
                request.DestinationType,
                request.ResourceType,
                request.DestinationField,
                request.ResourcePipelineRouteId,
                request.SourceSystem,
                request.SourceField,
                request.Order,
                request.OnNull,
                request.ErrorPolicy,
                request.OnNullDefaultValue,
                request.ArrayMode,
                request.FhirWriteBackJsonPath,
                request.ExecutionPhase,
                request.DeIdentificationProfileId,
                request.ExpectedValueType);
            rule.SetEnabled(request.IsEnabled);
            await _repository.AddAsync(rule, cancellationToken);
            return ToDto(rule);
        }

        existing.Update(
            configJson, request.Order, request.OnNull, request.ErrorPolicy,
            request.OnNullDefaultValue, request.ArrayMode, request.FhirWriteBackJsonPath,
            request.ExpectedValueType);
        existing.SetEnabled(request.IsEnabled);
        await _repository.UpdateAsync(existing, cancellationToken);
        return ToDto(existing);
    }

    public async Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await _repository.GetByIdAsync(ruleId, cancellationToken);
        if (rule is not null)
        {
            await _repository.DeleteAsync(rule, cancellationToken);
        }
    }

    public async Task<TransformPreviewResult> PreviewAsync(
        TransformPreviewRequest request, CancellationToken cancellationToken = default)
    {
        var rules = await _resolver.ResolveAsync(
            request.DestinationType, request.ResourceType, request.DestinationField,
            request.ResourcePipelineRouteId, request.SourceSystem, request.SourceField, cancellationToken);

        if (rules.Count == 0)
        {
            return new TransformPreviewResult(request.SampleValue, null, []);
        }

        var currentValue = request.SampleValue;
        var steps = new List<TransformStepTrace>();

        foreach (var rule in rules)
        {
            if (TransformNullPolicy.IsNullOrEmpty(currentValue))
            {
                var handled = TransformNullPolicy.Apply(rule, currentValue, out var stopChain);
                steps.Add(new TransformStepTrace(rule.NodeType, rule.Scope, currentValue, handled, true, null));
                currentValue = handled;
                if (stopChain)
                {
                    break;
                }

                continue;
            }

            var node = _nodeRegistry.Get(rule.NodeType);
            var config = JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ConfigJson) ?? [];
            config[ReservedTransformConfigKeys.DestinationType] = request.DestinationType.ToString();
            var secret = rule.NodeType is Domain.Enums.TransformNodeType.HashingMasking or Domain.Enums.TransformNodeType.DateMathAge
                ? _secretAccessor?.TransformHashingKey
                : null;
            var result = await TransformNodeApplier.ExecuteWithArrayModeAsync(node, currentValue, config, secret, rule.ArrayMode, cancellationToken);

            steps.Add(new TransformStepTrace(rule.NodeType, rule.Scope, currentValue, result.Value, result.Success, result.Error));

            if (result.Success)
            {
                currentValue = result.Value;
                continue;
            }

            currentValue = rule.ErrorPolicy switch
            {
                Domain.Enums.TransformErrorPolicy.PassThrough => currentValue,
                _ => null
            };

            if (rule.ErrorPolicy is Domain.Enums.TransformErrorPolicy.Fail or Domain.Enums.TransformErrorPolicy.RouteToDeadLetter)
            {
                break;
            }
        }

        return new TransformPreviewResult(currentValue, rules[0].Scope, steps);
    }

    public async Task<List<TransformationRuleDto>> GetEffectiveRulesAsync(
        DestinationType destinationType,
        string resourceType,
        string destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        string? sourceField,
        CancellationToken cancellationToken = default)
    {
        var rules = await _resolver.ResolveAsync(
            destinationType, resourceType, destinationField, resourcePipelineRouteId, sourceSystem, sourceField, cancellationToken);
        return rules.Select(ToDto).ToList();
    }

    public IReadOnlyList<TransformNodeSchemaDto> GetNodeSchemas() => TransformNodeConfigSchemas.All;

    public async Task<RuleImpactSummaryDto> GetRuleImpactSummaryAsync(
        TransformScope scope,
        string? resourceType,
        string? destinationField,
        CancellationToken cancellationToken = default)
    {
        if (scope is not (TransformScope.Global or TransformScope.ResourceType) || destinationField is null)
        {
            return new RuleImpactSummaryDto(0, 0);
        }

        var routes = await _configurationRepository.GetRoutesAsync(cancellationToken);
        var mappingProfilesById = (await _configurationRepository.GetMappingProfilesAsync(cancellationToken))
            .ToDictionary(m => m.Id);

        var matchingRoutes = routes
            .Where(r => mappingProfilesById.TryGetValue(r.MappingProfileId, out var mp) &&
                        (resourceType is null || mp.ResourceType == resourceType))
            .ToList();

        var withOverride = 0;
        foreach (var route in matchingRoutes)
        {
            var mappingResourceType = mappingProfilesById[route.MappingProfileId].ResourceType;

            var workflowRules = await _repository.GetWorkflowScopedAsync(
                route.Id, mappingResourceType, destinationField, sourceSystem: null, sourceField: null, cancellationToken);
            if (workflowRules.Count > 0)
            {
                withOverride++;
                continue;
            }

            var fieldRules = await _repository.GetFieldScopedAsync(
                mappingResourceType, destinationField, sourceSystem: null, sourceField: null, cancellationToken);
            if (fieldRules.Count > 0)
            {
                withOverride++;
            }
        }

        return new RuleImpactSummaryDto(matchingRoutes.Count, withOverride);
    }

    private static TransformationRuleDto ToDto(TransformationRule rule) => new(
        rule.Id,
        rule.Scope,
        rule.DestinationType,
        rule.ResourceType,
        rule.DestinationField,
        rule.ResourcePipelineRouteId,
        rule.SourceSystem,
        rule.SourceField,
        rule.NodeType,
        JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ConfigJson) ?? [],
        rule.Order,
        rule.OnNull,
        rule.ErrorPolicy,
        rule.IsEnabled,
        rule.OnNullDefaultValue,
        rule.ArrayMode,
        rule.FhirWriteBackJsonPath,
        rule.ExecutionPhase,
        rule.DeIdentificationProfileId,
        rule.ExpectedValueType);
}
