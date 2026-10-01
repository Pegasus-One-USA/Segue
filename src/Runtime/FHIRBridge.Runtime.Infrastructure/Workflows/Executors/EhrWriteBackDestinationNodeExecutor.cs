using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

/// <summary>
/// The EHR Write-Back destination node. Everything about the write is the shared destination executor's, except
/// the EHR itself: this executor resolves the connection named by the node's <c>dest_sourceConnectionId</c> (NOT the
/// upstream source, which is usually a different connection with a different client id), checks it may be written
/// to, and hands the writer an <see cref="IEhrWriteChannel"/> over it.
///
/// <para>Fails closed. A missing dependency, an unparseable connection id, a disabled or read-only connection, a vendor
/// with no write capability, or a connector without write support throws rather than letting the base fall back to
/// its placeholder "success".</para>
///
/// <para>The upstream source dependencies are passed to the base too, so the writer can fetch a record's patient from
/// the source when the batch does not carry it: <c>Patient/$match</c> needs the full demographics.</para>
/// </summary>
public sealed class EhrWriteBackDestinationNodeExecutor : DestinationNodeExecutor
{
    private readonly IConfiguredDestinationWriterFactory? _writerFactory;
    private readonly IConfigurationRepository? _configurationRepository;
    private readonly IFhirSourceClientFactory? _sourceClientFactory;
    private readonly ISourceConnectionRuntimeResolver? _sourceConnectionResolver;
    private readonly IFhirAccessTokenProvider? _accessTokenProvider;

    public EhrWriteBackDestinationNodeExecutor(
        IConfiguredDestinationWriterFactory? writerFactory = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IGovernanceLogger? governanceLogger = null,
        IConfigurationRepository? configurationRepository = null,
        IFhirSourceClientFactory? sourceClientFactory = null,
        ISourceConnectionRuntimeResolver? sourceConnectionResolver = null,
        IFhirAccessTokenProvider? accessTokenProvider = null)
        : base(
            WorkflowNodeTypes.EhrWriteBackDestination,
            DestinationType.EhrWriteBack,
            writerFactory,
            workflowDefinitionStore,
            governanceLogger,
            configurationRepository,
            sourceClientFactory,
            sourceConnectionResolver)
    {
        _writerFactory = writerFactory;
        _configurationRepository = configurationRepository;
        _sourceClientFactory = sourceClientFactory;
        _sourceConnectionResolver = sourceConnectionResolver;
        _accessTokenProvider = accessTokenProvider;
    }

    public override Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        if (_writerFactory is null || _configurationRepository is null || _sourceClientFactory is null || _sourceConnectionResolver is null)
        {
            throw new InvalidOperationException(
                "The EHR Write-Back node is not wired to the services it needs (writer, configuration, connector, resolver).");
        }

        return base.ExecuteAsync(context, node, inputs, cancellationToken);
    }

    protected override async Task<PipelineWriteContext> CompleteWriteContextAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        PipelineWriteContext writeContext,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(ReadStringConfiguration(node, "dest_sourceConnectionId"), out var targetId) || targetId == Guid.Empty)
        {
            throw new InvalidOperationException("The EHR Write-Back node does not name the EHR connection to write to.");
        }

        var connection = await _configurationRepository!.GetSourceConnectionAsync(targetId, cancellationToken)
            ?? throw new InvalidOperationException("The EHR connection this node writes to no longer exists.");
        if (!connection.IsEnabled)
        {
            throw new InvalidOperationException($"The EHR connection '{connection.Name}' is disabled.");
        }

        if (!connection.Access.AllowsWrite())
        {
            throw new InvalidOperationException(
                $"The EHR connection '{connection.Name}' is read-only. Set its Access to Write or Read & Write.");
        }

        var vendor = EhrWriteCapabilities.VendorProfile(connection.SourceSystemType)
            ?? throw new InvalidOperationException($"{connection.SourceSystemType} does not accept EHR write-back.");

        // A null application type is a legacy Backend row.
        var applicationType = connection.ApplicationType ?? FHIRBridge.SharedKernel.Enums.ApplicationType.Backend;
        if (!vendor.Capabilities.Any(capability => capability.AllowedApplicationTypes.Contains(applicationType)))
        {
            throw new InvalidOperationException(
                $"The EHR connection '{connection.Name}' uses the {applicationType} audience; {connection.SourceSystemType} write-back needs a Backend System connection.");
        }

        var source = await _sourceConnectionResolver!.ResolveAsync(targetId, null, null, cancellationToken)
            ?? throw new InvalidOperationException($"The EHR connection '{connection.Name}' could not be resolved.");

        // The write connection is never searched the way a source is, so none of the source-side search settings
        // apply. A vendor that ignores requested scopes (Epic) gets no scope at all; the granted scope is checked
        // instead.
        source = source with
        {
            SearchParameters = null,
            TargetPatientId = null,
            PatientIds = null,
            PatientSearchCriteria = null,
            SearchCriteriaByResourceType = null,
            OmitScopeParameter = !vendor.RequestsScopeOnTokenRequest,
            Scopes = vendor.RequestsScopeOnTokenRequest ? source.Scopes : [],
        };

        if (_sourceClientFactory!.Create(source.SourceType) is not IFhirWriteClient writeClient)
        {
            throw new InvalidOperationException($"The {source.SourceType} connector cannot send writes.");
        }

        Guid? destinationId = Guid.TryParse(ReadStringConfiguration(node, "destinationId"), out var parsedDestinationId)
            && parsedDestinationId != Guid.Empty
                ? parsedDestinationId
                : null;

        var channel = new FhirClientEhrWriteChannel(
            writeClient,
            source,
            _accessTokenProvider,
            targetId,
            connection.SourceSystemType,
            destinationId,
            ReadOptions(node));

        return writeContext with { EhrWriteChannel = channel };
    }

    /// <summary>The node's write-back settings. The portal stores every value as a string. Anything missing or
    /// unparseable falls to the safe side: a dry run, no patient creation, the default cap, preliminary notes.</summary>
    private static EhrWriteBackRunOptions ReadOptions(WorkflowNode node)
    {
        var dryRun = !bool.TryParse(ReadStringConfiguration(node, "dest_dryRun"), out var parsedDryRun) || parsedDryRun;
        var createPatient = bool.TryParse(ReadStringConfiguration(node, "dest_createPatientIfMissing"), out var parsedCreate) && parsedCreate;
        var maxWrites = int.TryParse(ReadStringConfiguration(node, "dest_maxWritesPerRun"), out var parsedMax) && parsedMax > 0
            ? Math.Min(parsedMax, EhrWriteBackRunOptions.MaxAllowedWritesPerRun)
            : EhrWriteBackRunOptions.DefaultMaxWritesPerRun;
        var docStatus = string.Equals(ReadStringConfiguration(node, "dest_noteDocStatus"), EhrWriteBackRunOptions.FinalDocStatus, StringComparison.OrdinalIgnoreCase)
            ? EhrWriteBackRunOptions.FinalDocStatus
            : EhrWriteBackRunOptions.PreliminaryDocStatus;
        var resources = (ReadStringConfiguration(node, "dest_resources") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new EhrWriteBackRunOptions(dryRun, createPatient, maxWrites, docStatus, resources);
    }
}
