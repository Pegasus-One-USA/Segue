using FHIRBridge.Runtime.Application.Workflows.Audit;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddScoped<IWorkflowAuditRecorder, InMemoryWorkflowAuditRecorder>();

        return services;
    }
}
