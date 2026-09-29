using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Exceptions;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed class ResourceTypeCriteriaService : IResourceTypeCriteriaService
{
    private readonly IResourceTypeCriteriaRepository _repository;

    public ResourceTypeCriteriaService(IResourceTypeCriteriaRepository repository)
    {
        _repository = repository;
    }

    public async Task<List<ResourceTypeCriteriaDto>> ListAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var rows = await _repository.ListForWorkflowAsync(workflowId, cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ResourceTypeCriteriaDto> SaveAsync(
        Guid workflowId, SaveResourceTypeCriteriaRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.SourceNodeId))
        {
            errors["sourceNodeId"] = ["Source node is required."];
        }

        if (string.IsNullOrWhiteSpace(request.ResourceType))
        {
            errors["resourceType"] = ["Resource type is required."];
        }

        // An append of nothing is a no-op the caller almost certainly didn't intend; a *replace* with an empty
        // string is how the UI clears criteria, so only the append path rejects it.
        if (string.IsNullOrWhiteSpace(request.Criteria) && !request.Replace)
        {
            errors["criteria"] = ["Criteria is required."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        var sourceNodeId = request.SourceNodeId.Trim();
        var resourceType = request.ResourceType.Trim();

        var existing = await _repository.GetAsync(workflowId, sourceNodeId, resourceType, cancellationToken);
        if (existing is null)
        {
            var created = new ResourceTypeCriteria(workflowId, sourceNodeId, resourceType, request.Criteria);
            await _repository.AddAsync(created, cancellationToken);
            return ToDto(created);
        }

        if (request.Replace)
        {
            existing.ReplaceCriteria(request.Criteria);
        }
        else
        {
            existing.AppendCriteria(request.Criteria);
        }

        await _repository.SaveChangesAsync(cancellationToken);
        return ToDto(existing);
    }

    public async Task DeleteAsync(
        Guid workflowId, string sourceNodeId, string resourceType, CancellationToken cancellationToken)
    {
        var existing = await _repository.GetAsync(
            workflowId, sourceNodeId.Trim(), resourceType.Trim(), cancellationToken)
            ?? throw new NotFoundException(nameof(ResourceTypeCriteria), $"{workflowId}/{sourceNodeId}/{resourceType}");

        await _repository.RemoveAsync(existing, cancellationToken);
    }

    public Task DeleteForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken) =>
        _repository.RemoveForWorkflowAsync(workflowId, cancellationToken);

    private static ResourceTypeCriteriaDto ToDto(ResourceTypeCriteria entity) =>
        new(entity.Id, entity.WorkflowId, entity.SourceNodeId, entity.ResourceType, entity.Criteria);
}
