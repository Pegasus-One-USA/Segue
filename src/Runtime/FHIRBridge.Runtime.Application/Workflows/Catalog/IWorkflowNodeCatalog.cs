namespace FHIRBridge.Runtime.Application.Workflows.Catalog;

public interface IWorkflowNodeCatalog
{
    IReadOnlyCollection<WorkflowNodeCatalogItem> List();

    WorkflowNodeCatalogItem? Find(string nodeType);
}
