using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public sealed class UsCoreValidationNodeExecutor : PassThroughNodeExecutor
{
    public UsCoreValidationNodeExecutor(FHIRBridge.Application.Abstractions.Normalization.IResourceNormalizationService? normalizationService = null)
        : base(WorkflowNodeTypes.UsCoreValidation, WorkflowDataContract.NormalizedResourceBatch, normalizationService)
    {
    }
}

public sealed class ConsentNodeExecutor : PassThroughNodeExecutor
{
    private readonly IConsentService? _consentService;
    private readonly IGovernancePolicyService? _governancePolicyService;

    public ConsentNodeExecutor(
        IConsentService? consentService = null,
        IGovernancePolicyService? governancePolicyService = null)
        : base(WorkflowNodeTypes.Consent, WorkflowDataContract.MappedRecordBatch)
    {
        _consentService = consentService;
        _governancePolicyService = governancePolicyService;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        if (_consentService is null && _governancePolicyService is null)
        {
            return await base.ExecuteAsync(context, node, inputs, cancellationToken);
        }

        var resourceInputs = PassThroughNodeExecutor.ReadResourceEnvelopes(inputs);
        if (resourceInputs.Count > 0)
        {
            var permittedResources = new List<ResourceEnvelope>();
            foreach (var resource in resourceInputs)
            {
                if (await IsPermittedAsync(context, resource.ResourceType, resource.ResourceId, cancellationToken))
                {
                    permittedResources.Add(resource);
                }
            }

            return new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                new ResourceBatch(permittedResources),
                WorkflowDataContract.ResourceBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["count"] = permittedResources.Count
                });
        }

        var permitted = new List<MappedDestinationRecord>();
        foreach (var record in ReadMappedRecords(inputs))
        {
            if (await IsPermittedAsync(context, record.ResourceType, record.SourceResourceId, cancellationToken))
            {
                permitted.Add(record);
            }
        }

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new MappedRecordBatch(permitted),
            WorkflowDataContract.MappedRecordBatch,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["count"] = permitted.Count
            });
    }

    private async Task<bool> IsPermittedAsync(
        WorkflowExecutionContext context,
        string resourceType,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        var consentDecision = _consentService?.Evaluate(resourceType, resourceId);
        if (consentDecision is { IsPermitted: false })
        {
            return false;
        }

        if (_governancePolicyService is null)
        {
            return true;
        }

        var governanceDecision = await _governancePolicyService.EvaluateAsync(
            new ResourceGovernanceContext(
                context.WorkflowRunId,
                null,
                resourceType,
                resourceId,
                "WorkflowNodeExecute",
                null,
                context.CorrelationId),
            cancellationToken);

        return governanceDecision.IsAllowed;
    }
}

public sealed class DeIdentificationNodeExecutor : WorkflowNodeExecutorBase
{
    private readonly IDeIdentificationService? _deIdentificationService;
    private readonly IDataSetDeIdentificationService? _dataSetDeIdentificationService;
    private readonly IConfigurationRepository? _configurationRepository;

    public DeIdentificationNodeExecutor(
        IDeIdentificationService? deIdentificationService = null,
        IDataSetDeIdentificationService? dataSetDeIdentificationService = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
        IConfigurationRepository? configurationRepository = null)
        : base(WorkflowNodeTypes.DeIdentification, WorkflowDataContract.DeIdentifiedBatch, loggerFactory)
    {
        _deIdentificationService = deIdentificationService;
        _dataSetDeIdentificationService = dataSetDeIdentificationService;
        _configurationRepository = configurationRepository;
    }

    /// <summary>
    /// Which <c>DeIdentificationProfile</c> this node's rules come from, most specific first:
    /// <list type="number">
    /// <item>the node's own <c>profileId</c> config, when something has set it;</item>
    /// <item><b>the profile assigned to this node's destination</b> — the chain-node patch stamps
    /// <c>destinationId</c> onto every chain node, so the De-identification node can resolve the same
    /// <c>DestinationConfiguration.DeIdentificationProfileId</c> the portal's picker writes and the route plane's
    /// <c>DestinationSensitivityGovernanceRule</c> reads;</item>
    /// <item>the seeded default profile, as before.</item>
    /// </list>
    ///
    /// Step 2 is the one that makes the feature work at all on this plane. Without it the destination-assigned
    /// profile — the only thing the UI actually lets you choose — was never consulted by a DAG run: nothing
    /// writes <c>profileId</c>, so every node fell through to <c>DefaultProfileId</c>, whose row does not exist
    /// while the seeder is disabled. The rule query then returned nothing and every resource passed through
    /// unredacted while the run reported success. Resolving from the destination here (rather than only stamping
    /// the id at save time in the portal) also repairs graphs that were already saved, with no re-save needed.
    /// </summary>
    private async Task<Guid> ResolveProfileIdAsync(WorkflowNode node, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(ReadStringConfiguration(node, "profileId"), out var explicitProfileId) && explicitProfileId != Guid.Empty)
        {
            return explicitProfileId;
        }

        if (_configurationRepository is not null
            && Guid.TryParse(ReadStringConfiguration(node, "destinationId"), out var destinationId)
            && destinationId != Guid.Empty)
        {
            var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
            if (destination?.DeIdentificationProfileId is { } destinationProfileId && destinationProfileId != Guid.Empty)
            {
                return destinationProfileId;
            }
        }

        return FHIRBridge.Domain.Entities.DeIdentificationProfile.DefaultProfileId;
    }

    public override async Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var resourceInputs = PassThroughNodeExecutor.ReadResourceEnvelopes(inputs).ToArray();
        var records = PassThroughNodeExecutor.ReadMappedRecords(inputs).ToArray();

        // Opt-out flag: a graph can carry raw (non-de-identified) data through this node while still satisfying the
        // DeIdentifiedBatch output contract. Defaults to true so existing behaviour is unchanged. The route→graph
        // projection sets it false so a launch mirrors the route path (which de-identifies only when governance asks).
        var deIdentifyEnabled = ReadBoolConfiguration(node, "deIdentify") ?? true;
        var resolvedProfileId = await ResolveProfileIdAsync(node, cancellationToken);

        if (!deIdentifyEnabled || (_deIdentificationService is null && _dataSetDeIdentificationService is null))
        {
            // Pass through unchanged (preserving resource envelopes when present) rather than the base placeholder,
            // so downstream mapping receives the real payloads.
            var passThroughRecords = resourceInputs.Length > 0
                ? (IReadOnlyCollection<object>)resourceInputs
                : records;

            return new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                new DeIdentifiedBatch(passThroughRecords),
                WorkflowDataContract.DeIdentifiedBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["deIdentified"] = false,
                    ["count"] = passThroughRecords.Count
                });
        }

        if (resourceInputs.Length > 0)
        {
            var deIdentifiedResources = new List<ResourceEnvelope>();
            // Threaded onto this node's output metadata (see WorkflowNodeOutputMetadataKeys.PreMappingRedactions)
            // so the downstream Mapping node can merge each field's PreMapping redaction into the same Field
            // Lineage chain as its own PostMapping hops — SafeHarborDeIdentificationService.DeIdentifyAsync is
            // the only place that knows what was actually changed at this stage.
            var redactionsByResourceId = new Dictionary<string, IReadOnlyList<DeIdentificationFieldHop>>();
            foreach (var resource in resourceInputs)
            {
                string sourceJson;
                if (_deIdentificationService is null)
                {
                    sourceJson = Convert.ToString(resource.Payload) ?? "{}";
                }
                else
                {
                    var result = await _deIdentificationService.DeIdentifyAsync(
                        new DeIdentificationRequest(
                            resource.ResourceType,
                            resource.ResourceId,
                            Convert.ToString(resource.Payload) ?? "{}",
                            [],
                            resolvedProfileId),
                        cancellationToken);
                    sourceJson = result.Json;
                    if (result.Hops.Count > 0)
                    {
                        redactionsByResourceId[resource.ResourceId] = result.Hops;
                    }
                }

                deIdentifiedResources.Add(resource with { Payload = sourceJson });
            }

            // De-identification was asked for and nothing at all was redacted. Almost always a misconfiguration
            // — a profile with no PreMapping rules, or rules whose resourceType doesn't match what this run
            // carried — and until now it looked exactly like success: unredacted PHI at the destination, a green
            // run, and no way to tell from the outside. Surfaced in metadata as well as the log so Execution
            // History can show it rather than requiring someone to diff the written rows against the source.
            if (redactionsByResourceId.Count == 0 && deIdentifiedResources.Count > 0)
            {
                Logger.LogWarning(
                    "De-identification node {NodeId} applied no redactions to {ResourceCount} resource(s) using profile "
                    + "{ProfileId}. The resources were written through unredacted — check that the profile has "
                    + "PreMapping rules and that their resourceType matches the data in this run.",
                    node.Id, deIdentifiedResources.Count, resolvedProfileId);
            }

            return new WorkflowNodeOutput(
                node.Id,
                node.NodeType,
                new DeIdentifiedBatch(deIdentifiedResources),
                WorkflowDataContract.DeIdentifiedBatch,
                new Dictionary<string, object?>
                {
                    ["executor"] = GetType().Name,
                    ["count"] = deIdentifiedResources.Count,
                    // Lets Execution History show "de-identification ran but changed nothing" without anyone
                    // having to diff written rows against the source.
                    ["redactedResourceCount"] = redactionsByResourceId.Count,
                    [WorkflowNodeOutputMetadataKeys.PreMappingRedactions] = redactionsByResourceId,
                });
        }

        var deIdentified = new List<MappedDestinationRecord>();

        if (_dataSetDeIdentificationService is not null && records.Length > 0)
        {
            var byResourceType = records.GroupBy(record => record.ResourceType);
            foreach (var group in byResourceType)
            {
                var result = await _dataSetDeIdentificationService.DeIdentifyAsync(
                    new DataSetDeIdentificationRequest(
                        group.Key,
                        group.Select(record => record.SourceJson ?? "{}").ToArray()),
                    cancellationToken);

                var paired = group.Zip(result.ResourcesJson);
                foreach (var (record, sourceJson) in paired)
                {
                    deIdentified.Add(record with { SourceJson = sourceJson });
                }
            }
        }
        else if (_deIdentificationService is not null)
        {
            foreach (var record in records)
            {
                var result = await _deIdentificationService.DeIdentifyAsync(
                    new DeIdentificationRequest(
                        record.ResourceType,
                        record.SourceResourceId,
                        record.SourceJson ?? "{}",
                        [],
                        resolvedProfileId),
                    cancellationToken);

                deIdentified.Add(record with { SourceJson = result.Json });
            }
        }

        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
            Logger,
            FHIRBridge.Observability.Logging.LogEvents.GovernanceApplied,
            "De-identification applied to {RecordCount} record(s) using profile {DeIdentificationProfileId}.",
            deIdentified.Count, resolvedProfileId);

        return new WorkflowNodeOutput(
            node.Id,
            node.NodeType,
            new DeIdentifiedBatch(deIdentified),
            WorkflowDataContract.DeIdentifiedBatch,
            new Dictionary<string, object?>
            {
                ["executor"] = GetType().Name,
                ["count"] = deIdentified.Count
            });
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new DeIdentifiedBatch(PassThroughNodeExecutor.ReadMappedRecords(inputs).ToArray());
}

public sealed class AuditLineageNodeExecutor : WorkflowNodeExecutorBase
{
    public AuditLineageNodeExecutor()
        : base(WorkflowNodeTypes.AuditLineage, WorkflowDataContract.AuditResult)
    {
    }

    protected override object CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs)
        => new AuditResult(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
}
