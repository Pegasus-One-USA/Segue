using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>
/// Durable, multi-instance idempotency store backed by the <c>ProcessedMessages</c> table. The primary key on
/// MessageId makes "mark processed" atomic across worker instances: a concurrent insert of the same id loses the
/// race with a unique-constraint violation, which is treated as "already processed".
/// </summary>
public sealed class EfProcessedMessageStore : IProcessedMessageStore
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfProcessedMessageStore(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> TryMarkProcessedAsync(string messageId, CancellationToken cancellationToken)
    {
        var alreadyProcessed = await _dbContext.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(x => x.MessageId == messageId, cancellationToken);

        if (alreadyProcessed)
        {
            return false;
        }

        _dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = messageId,
            ProcessedOnUtc = DateTime.UtcNow
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Another instance inserted the same id between the check and the save — treat as already processed.
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException && sqlException.Number is 2627 or 2601)
            {
                return true;
            }
        }

        return false;
    }
}
