namespace FHIRBridge.Runtime.Domain.Workflows;

public static class WorkflowRankPolicy
{
    public static bool IsRankValidForCategory(WorkflowNodeCategory category, int rank)
        => category switch
        {
            WorkflowNodeCategory.Source => rank == 0,
            WorkflowNodeCategory.Transform => rank is > 0 and < 100,
            WorkflowNodeCategory.Compliance => rank is > 0 and < 100,
            WorkflowNodeCategory.Destination => rank is > 0 and < 100,
            WorkflowNodeCategory.Analytics => rank is > 0 and < 100,
            _ => false
        };
}
