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
    private readonly IAppSecretAccessor? _secretAccessor;

    public TransformationRuleService(
        ITransformationRuleRepository repository,
        IEffectiveRuleResolver resolver,
        ITransformNodeRegistry nodeRegistry,
        IAppSecretAccessor? secretAccessor = null)
    {
        _repository = repository;
        _resolver = resolver;
        _nodeRegistry = nodeRegistry;
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
                request.ArrayMode);
            rule.SetEnabled(request.IsEnabled);
            await _repository.AddAsync(rule, cancellationToken);
            return ToDto(rule);
        }

        existing.Update(configJson, request.Order, request.OnNull, request.ErrorPolicy, request.OnNullDefaultValue, request.ArrayMode);
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
            var secret = rule.NodeType is Domain.Enums.TransformNodeType.HashingMasking or Domain.Enums.TransformNodeType.DateMathAge
                ? _secretAccessor?.TransformHashingKey
                : null;
            var result = TransformNodeApplier.ExecuteWithArrayMode(node, currentValue, config, secret, rule.ArrayMode);

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
        rule.ArrayMode);
}
