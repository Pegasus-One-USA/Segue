using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FHIRBridge.Runtime.Infrastructure.Workflows;

public static class WorkflowInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, EpicSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, CernerSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, AthenahealthSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, AllscriptsSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, EClinicalWorksSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, MeditechSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, GenericFhirSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, Hl7v2MllpSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, SampleSourceNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, NormalizationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, DataQualityScoringNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, FlattenExtensionsNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, PatientMatchingNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, MappingNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, RepeatingArrayMappingNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, TerminologyNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, TerminologyValidateNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, TerminologyLookupNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, TerminologyTranslateNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, TerminologyExpandNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, UsCoreValidationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, ConsentNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, DeIdentificationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, AuditLineageNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, SqlServerDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, AzureSqlDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, PostgreSqlDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, MySqlDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, SnowflakeDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, BlobDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, S3DestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, FhirRepositoryDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, CsvDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, ExcelDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, NdjsonDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, ParquetDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, AvroDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, ProtobufDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, PdfDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, SftpDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, RestApiDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, InMemoryDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, WebhookNotifierNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, PowerBiDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, TableauDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, DatabricksDestinationNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, HedisMeasureReportNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, AnomalyDetectionNodeExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWorkflowNodeExecutor, PatientAggregationNodeExecutor>());

        services.AddHttpClient(nameof(WebhookNotifierNodeExecutor));

        // Wraps every registered executor so a workflow-graph run reports lineage the same way the Configured
        // Pipeline path does. Registered after AddWorkflowCore()'s plain WorkflowNodeExecutorRegistry so this wins
        // the single-instance resolution (same "last registration wins" pattern used for the SQL-backed stores).
        // Falls back to the unwrapped registry when no ILineageTracker is registered (e.g. a composition root that
        // never called AddFHIRBridgeInfrastructure), matching the optional-dependency style used elsewhere.
        services.AddScoped<IWorkflowNodeExecutorRegistry>(serviceProvider =>
        {
            var executors = serviceProvider.GetServices<IWorkflowNodeExecutor>();
            var lineageTracker = serviceProvider.GetService<ILineageTracker>();

            return lineageTracker is null
                ? new WorkflowNodeExecutorRegistry(executors)
                : new LineageTrackingWorkflowNodeExecutorRegistry(executors, lineageTracker);
        });

        return services;
    }
}
