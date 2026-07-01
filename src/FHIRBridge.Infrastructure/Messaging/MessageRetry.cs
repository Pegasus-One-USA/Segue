using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>Runs an action with exponential-backoff retries; rethrows the last exception once attempts are exhausted.</summary>
internal static class MessageRetry
{
    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        MessageProcessingOptions options,
        ILogger logger,
        string context,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, options.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action(cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                var baseDelay = Math.Max(1, options.BaseDelayMilliseconds);
                var delayMs = (baseDelay * Math.Pow(2, attempt - 1)) + Random.Shared.Next(0, baseDelay);

                logger.LogWarning(
                    exception,
                    "{Context} failed on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs} ms.",
                    context, attempt, maxAttempts, (int)delayMs);

                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
        }
    }
}
