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
///
/// <para><b>Test runs.</b> <c>dest_testAsVendor</c> names the vendor a Generic FHIR connection stands in for. The
/// channel is then the TESTED vendor's, built over the test server's connection, so the writer shapes and sends exactly
/// what it would for the vendor; only the connection's own settings (its audience, its scopes) are the test
/// server's.</para>
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
                $"The EHR connection '{connection.Name}' is read-only. Choose a connection listed under Destination Connections > EHR write connections.");
        }

        var vendor = EhrWriteCapabilities.VendorProfile(connection.SourceSystemType)
            ?? throw new InvalidOperationException($"{connection.SourceSystemType} does not accept EHR write-back.");

        var options = ReadOptions(node, connection.DepartmentId);
        if (options.TestAsVendor is { } tested && connection.SourceSystemType != EhrWriteCapabilities.TestServerType)
        {
            // A test run must never reach a real EHR: only a Generic FHIR connection can be the test server.
            throw new InvalidOperationException(
                $"The EHR Write-Back node tests as {tested}, which needs a Generic FHIR connection as the test server; " +
                $"'{connection.Name}' is a {connection.SourceSystemType} connection.");
        }

        var writeVendor = options.TestAsVendor ?? connection.SourceSystemType;

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
            Scopes = vendor.RequestsScopeOnTokenRequest ? EhrWriteChannels.WriteScopes(source.Scopes, vendor.ProprietaryApiScope) : [],
        };

        if (_sourceClientFactory!.Create(source.SourceType) is not IFhirWriteClient writeClient)
        {
            throw new InvalidOperationException($"The {source.SourceType} connector cannot send writes.");
        }

        if (options.TestAsVendor is { } testedVendor)
        {
            writeClient = EhrWriteChannels.TestClient(testedVendor, writeClient, source);
        }

        Guid? destinationId = Guid.TryParse(ReadStringConfiguration(node, "destinationId"), out var parsedDestinationId)
            && parsedDestinationId != Guid.Empty
                ? parsedDestinationId
                : null;

        var fhirChannel = new FhirClientEhrWriteChannel(
            writeClient,
            source,
            _accessTokenProvider,
            targetId,
            writeVendor,
            destinationId,
            options,
            connection.VendorWriteApisActivated);

        return writeContext with { EhrWriteChannel = EhrWriteChannels.For(writeVendor, fhirChannel) };
    }

    /// <summary>The node's write-back settings. The portal stores every value as a string. Anything missing or
    /// unparseable falls to the safe side: a dry run, no patient creation, no clone mode, no holder encounters, the
    /// default cap, preliminary notes. Clone mode is checked against its system setting by the writer. The department
    /// is the node's own <c>dest_targetDepartmentId</c> when set, else the write connection's.</summary>
    private static EhrWriteBackRunOptions ReadOptions(WorkflowNode node, string? connectionDepartmentId)
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

        var cloneMode = bool.TryParse(ReadStringConfiguration(node, "dest_cloneMode"), out var parsedClone) && parsedClone;
        var holderEncounter = bool.TryParse(ReadStringConfiguration(node, "dest_createHolderEncounter"), out var parsedHolder) && parsedHolder;
        var providerId = ReadStringConfiguration(node, "dest_targetProviderId") is { } provider && !string.IsNullOrWhiteSpace(provider) ? provider.Trim() : null;
        var departmentId = ReadStringConfiguration(node, "dest_targetDepartmentId") is { } department && !string.IsNullOrWhiteSpace(department)
            ? department.Trim()
            : string.IsNullOrWhiteSpace(connectionDepartmentId) ? null : connectionDepartmentId.Trim();
        var variants = (ReadStringConfiguration(node, "dest_enabledVariants") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new EhrWriteBackRunOptions(
            dryRun, createPatient, maxWrites, docStatus, resources, cloneMode, holderEncounter, providerId, departmentId,
            ReadTestAsVendor(node), variants);
    }

    /// <summary>The vendor a test run stands in for, or null for a real write. A value that names no testable vendor
    /// fails the run rather than writing for real.</summary>
    private static SourceSystemType? ReadTestAsVendor(WorkflowNode node)
    {
        var raw = ReadStringConfiguration(node, "dest_testAsVendor");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return EhrWriteCapabilities.TryParseVendor(raw, out var vendor) && EhrWriteCapabilities.TestableVendors.Contains(vendor)
            ? vendor
            : throw new InvalidOperationException($"The EHR Write-Back node tests as '{raw}', which is not a vendor that can be tested.");
    }
}
