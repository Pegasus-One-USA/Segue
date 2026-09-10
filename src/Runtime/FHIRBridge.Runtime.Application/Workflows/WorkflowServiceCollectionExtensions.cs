using FHIRBridge.Runtime.Application.Workflows.Audit;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using Microsoft.Extensions.DependencyInjection;
using FHIRBridge.Runtime.Application.Workflows.Validation;

namespace FHIRBridge.Runtime.Application.Workflows;

public static class WorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowCore(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowNodeCatalog, DefaultWorkflowNodeCatalog>();
        services.AddScoped<IWorkflowGraphValidator, WorkflowGraphValidator>();
        services.AddScoped<IWorkflowNodeExecutorRegistry, WorkflowNodeExecutorRegistry>();
        services.AddScoped<IRankedWorkflowOrchestrator, RankedWorkflowOrchestrator>();
        services.AddSingleton<IWorkflowDefinitionStore, InMemoryWorkflowDefinitionStore>();
        services.AddSingleton<IWorkflowRunStore, InMemoryWorkflowRunStore>();
        services.AddSingleton<IWorkflowNodeResourceHistoryRecorder, InMemoryWorkflowNodeResourceHistoryRecorder>();
        services.AddScoped<IWorkflowAuditRecorder, InMemoryWorkflowAuditRecorder>();
        services.AddSingleton<IWorkflowRunTracker, InMemoryWorkflowRunTracker>();

        // validate-run's rule set. Registered as a collection so a new rule is a new class plus one line here —
        // never an edit to a switch — matching how source vendors and application types are already extended.
        // The vendor/resource-type-specific rules (e.g. "Epic requires category on Observation") slot in here.
        services.AddScoped<IWorkflowRunValidator, WorkflowRunValidator>();
        services.AddScoped<IWorkflowRunParameterRule, WorkflowIsRunnableRule>();

        return services;
    }
}
