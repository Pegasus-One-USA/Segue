using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Exceptions;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed class DeIdentificationProfileService : IDeIdentificationProfileService
{
    private readonly IDeIdentificationProfileRepository _repository;
    private readonly IDeIdentificationService _deIdentificationService;
    private readonly ITransformationRuleRepository _ruleRepository;

    public DeIdentificationProfileService(
        IDeIdentificationProfileRepository repository,
        IDeIdentificationService deIdentificationService,
        ITransformationRuleRepository ruleRepository)
    {
        _repository = repository;
        _deIdentificationService = deIdentificationService;
        _ruleRepository = ruleRepository;
    }

    public async Task<List<DeIdentificationProfileDto>> ListAsync(CancellationToken cancellationToken)
    {
        var profiles = await _repository.ListAsync(cancellationToken);
        return profiles.Select(ToDto).ToList();
    }

    public async Task<DeIdentificationProfileDto> CreateAsync(
        CreateDeIdentificationProfileRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["name"] = ["Name is required."]
            });
        }

        var profile = new DeIdentificationProfile(request.Name.Trim(), request.Description);
        await _repository.AddAsync(profile, cancellationToken);
        return ToDto(profile);
    }

    public async Task<DeIdentificationPreviewResult> PreviewAsync(
        Guid profileId, DeIdentificationPreviewRequest request, CancellationToken cancellationToken)
    {
        var profile = await _repository.GetByIdAsync(profileId, cancellationToken)
            ?? throw new NotFoundException(nameof(DeIdentificationProfile), profileId);

        try
        {
            JsonDocument.Parse(request.SampleJson);
        }
        catch (JsonException exception)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["sampleJson"] = [$"Sample resource is not valid JSON: {exception.Message}"]
            });
        }

        var redacted = await _deIdentificationService.DeIdentifyAsync(
            new DeIdentificationRequest(request.ResourceType, null, request.SampleJson, [], profile.Id),
            cancellationToken);

        return new DeIdentificationPreviewResult(redacted.Json);
    }

    public async Task<WorkflowDeIdentificationRuleDeletionResult> DeleteWorkflowPolicyRulesAsync(
        Guid workflowId, CancellationToken cancellationToken)
    {
        // The workflow's own policy is the one named with its id — the only link a policy has to a workflow.
        var workflowName = workflowId.ToString();
        var profiles = await _repository.ListAsync(cancellationToken);
        var owned = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, workflowName, StringComparison.OrdinalIgnoreCase));
        if (owned is null)
        {
            // No policy of its own means no rules of its own: nothing to delete, which is a success.
            return new WorkflowDeIdentificationRuleDeletionResult(0, []);
        }

        var usedElsewhere = await _repository.FindUsesOutsideWorkflowAsync(owned.Id, workflowId, cancellationToken);
        if (usedElsewhere.Count > 0)
        {
            return new WorkflowDeIdentificationRuleDeletionResult(0, usedElsewhere);
        }

        var deleted = await _ruleRepository.DeleteDeIdentificationProfileRulesAsync(owned.Id, cancellationToken);
        return new WorkflowDeIdentificationRuleDeletionResult(deleted, []);
    }

    private static DeIdentificationProfileDto ToDto(DeIdentificationProfile profile) =>
        new(profile.Id, profile.Name, profile.Description);
}
