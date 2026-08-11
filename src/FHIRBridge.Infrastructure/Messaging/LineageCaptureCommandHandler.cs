using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// Persists a <see cref="LineageCaptureCommand"/>'s buffered hops into <c>FieldLineageEntries</c>. This is the
/// only place a field-lineage row is ever written to SQL — the transform executor that produced the hops never
/// waits on this call; it only waits on <see cref="ILineageCaptureDispatcher.EnqueueAsync"/> handing the batch to
/// the transport. Deduplicates by <see cref="LineageCaptureCommand.MessageId"/> like every other message handler
/// in this codebase (see <see cref="PipelineRunCommandHandler"/>) since a redelivered message must not double-insert.
/// </summary>
public sealed class LineageCaptureCommandHandler : ILineageCaptureCommandHandler
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IProcessedMessageStore _processedMessageStore;
    private readonly ILogger<LineageCaptureCommandHandler> _logger;

    public LineageCaptureCommandHandler(
        FHIRBridgeDbContext dbContext,
        IProcessedMessageStore processedMessageStore,
        ILogger<LineageCaptureCommandHandler> logger)
    {
        _dbContext = dbContext;
        _processedMessageStore = processedMessageStore;
        _logger = logger;
    }

    public async Task HandleAsync(LineageCaptureCommand command, CancellationToken cancellationToken)
    {
        if (!await _processedMessageStore.TryMarkProcessedAsync(command.MessageId, cancellationToken))
        {
            _logger.LogInformation("Skipping already-processed lineage capture command {MessageId}.", command.MessageId);
            return;
        }

        var recordedAtUtc = DateTimeOffset.UtcNow;
        var entries = command.Entries.Select(entry => new FieldLineageEntry(
            Guid.NewGuid(),
            command.WorkflowRunId,
            command.WorkflowNodeId,
            command.ResourceType,
            command.ResourceId,
            entry.DestinationField,
            entry.SourceField,
            entry.NodeOrder,
            entry.NodeType,
            entry.ConfigJson,
            entry.SourceValueJson,
            entry.DestinationValueJson,
            entry.Success,
            entry.ErrorMessage,
            entry.DurationMs,
            recordedAtUtc));

        await _dbContext.FieldLineageEntries.AddRangeAsync(entries, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
