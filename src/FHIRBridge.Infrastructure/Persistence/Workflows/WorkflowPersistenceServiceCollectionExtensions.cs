using FHIRBridge.Infrastructure.Persistence.Pipeline;
using FHIRBridge.Infrastructure.Workflows;
using FHIRBridge.Runtime.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.Workflows.Audit;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

public static class WorkflowPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Scenario A: makes the ranked-workflow graph engine durable by backing its stores with the
    /// control-plane <see cref="FHIRBridgeDbContext"/>. Call this AFTER <c>AddWorkflowCore()</c> so these
    /// SQL registrations override the in-memory defaults it registers (last registration wins). The rest
    /// of the engine — orchestrator, validator, executor registry, catalog — is untouched.
    ///
    /// Also wires the Scenario B launch-graph path (route→graph projection + resolver + feature flag), which
    /// only makes sense once graphs are durably persisted. The flag defaults OFF, so launches keep using the
    /// route path until an operator opts in per source.
    /// </summary>
    public static IServiceCollection AddWorkflowSqlPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Scoped, because both stores depend on the scoped FHIRBridgeDbContext.
        services.AddScoped<IWorkflowDefinitionStore, SqlWorkflowDefinitionStore>();
        services.AddScoped<IWorkflowRunStore, SqlWorkflowRunStore>();
        services.AddScoped<IWorkflowNodeResourceHistoryRecorder, EfWorkflowNodeResourceHistoryRecorder>();
        // HIPAA #4: durable, append-only workflow audit trail — overrides AddWorkflowCore()'s in-memory default.
        services.AddScoped<IWorkflowAuditRecorder, EfWorkflowAuditRecorder>();

        // Runtime DAG plane (bulk-export/PipelineOrchestrator, a separate execution path from the ranked-workflow
        // graph engine above) — overrides AddRuntimeInfrastructure()'s in-memory IPipelineRunStore default. Lives
        // here rather than a separate extension method because it needs the exact same FHIRBridgeDbContext-
        // availability gating this whole method is already called behind (see the call site's remarks).
        services.AddScoped<IPipelineRunStore, SqlPipelineRunStore>();

        // Scenario B: gate + project + resolve the launch graph. Registered here so ILaunchWorkflowResolver's
        // dependency on the (SQL) IWorkflowDefinitionStore is always satisfiable; hosts that don't opt into
        // persistence simply don't register the resolver, and the launch seam falls back to the route path.
        services.Configure<WorkflowGraphExecutionOptions>(
            configuration.GetSection(WorkflowGraphExecutionOptions.SectionName));
        services.AddScoped<ILaunchWorkflowProjection, RouteToWorkflowGraphProjection>();
        services.AddScoped<ILaunchWorkflowResolver, LaunchWorkflowResolver>();

        return services;
    }
}
