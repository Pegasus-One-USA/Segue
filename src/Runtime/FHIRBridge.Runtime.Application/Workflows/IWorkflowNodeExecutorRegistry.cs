namespace FHIRBridge.Runtime.Application.Workflows;

public interface IWorkflowNodeExecutorRegistry
{
    IWorkflowNodeExecutor? Get(string nodeType);

    IWorkflowNodeExecutor GetRequired(string nodeType);
}
