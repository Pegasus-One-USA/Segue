namespace FHIRBridge.Runtime.Domain.Workflows;

public enum WorkflowNodeCategory
{
    Source = 0,
    Transform = 10,
    Compliance = 20,
    Destination = 30,
    Analytics = 40
}
