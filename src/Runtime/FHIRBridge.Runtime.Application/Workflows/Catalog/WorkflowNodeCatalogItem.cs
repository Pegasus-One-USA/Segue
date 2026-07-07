using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Catalog;

public sealed record WorkflowNodeCatalogItem(
    string NodeType,
    WorkflowNodeCategory Category,
    int Rank,
    IReadOnlyCollection<string> RequiredConfigurationFields,
    IReadOnlyCollection<WorkflowDataContract> InputContracts,
    WorkflowDataContract OutputContract,
    string ExecutorKey,
    string DisplayName,
    string TransformId,
    string Description);
