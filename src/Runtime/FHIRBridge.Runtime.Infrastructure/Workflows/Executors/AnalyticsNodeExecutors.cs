using FHIRBridge.Application.Abstractions.Aggregation;
using FHIRBridge.Application.Services;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public sealed class HedisMeasureReportNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly IHedisMeasureReportService? _hedisMeasureReportService;

    public HedisMeasureReportNodeExecutor(IHedisMeasureReportService? hedisMeasureReportService = null)
        : base(WorkflowNodeTypes.HedisMeasureReport, WorkflowDataContract.AuditResult)
    {
        _hedisMeasureReportService = hedisMeasureReportService;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var measureId = ReadStringConfiguration(node, "measureId") ?? "FHIRBridge-PIPELINE-SUCCESS";
        object? result = _hedisMeasureReportService is null
            ? null
            : await _hedisMeasureReportService.GenerateAsync(
                context.TenantId,
                measureId,
                DateTime.UtcNow.AddDays(-30),
                DateTime.UtcNow,
                cancellationToken);
        result ??= new AuditResult(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            result,
            WorkflowDataContract.AuditResult);
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new AuditResult(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
}

public sealed class AnomalyDetectionNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly IAnomalyDetectionService? _anomalyDetectionService;

    public AnomalyDetectionNodeExecutor(IAnomalyDetectionService? anomalyDetectionService = null)
        : base(WorkflowNodeTypes.AnomalyDetection, WorkflowDataContract.AuditResult)
    {
        _anomalyDetectionService = anomalyDetectionService;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        object? result = _anomalyDetectionService is null
            ? null
            : await _anomalyDetectionService.AnalyzeRunsAsync(context.TenantId, 200, cancellationToken);
        result ??= new AuditResult(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            result,
            WorkflowDataContract.AuditResult);
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new AuditResult(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
}

public sealed class PatientAggregationNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly IPatientAggregationService? _patientAggregationService;

    public PatientAggregationNodeExecutor(IPatientAggregationService? patientAggregationService = null)
        : base(WorkflowNodeTypes.PatientAggregation, WorkflowDataContract.AuditResult)
    {
        _patientAggregationService = patientAggregationService;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var patientId = ReadStringConfiguration(node, "patientId");
        object? result = _patientAggregationService is null || string.IsNullOrWhiteSpace(patientId)
            ? null
            : await _patientAggregationService.GetEverythingAsync(
                context.TenantId,
                patientId,
                ["Patient", "Observation", "Condition", "Encounter"],
                null,
                cancellationToken);
        result ??= new AuditResult(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            result,
            WorkflowDataContract.AuditResult);
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new AuditResult(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
}
