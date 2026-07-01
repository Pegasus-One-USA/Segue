namespace FHIRBridge.Runtime.Application.Pipeline;

/// <summary>
/// Bounded fan-out / fan-in helper. Runs a projection over many items concurrently with a cap on the degree of
/// parallelism, then joins all results back in the original item order (structured fan-in). Use it for the I/O-bound,
/// independent parts of a pipeline (e.g. extracting several resource types) instead of a sequential <c>foreach</c>.
/// </summary>
public static class ParallelFanOut
{
    /// <summary>
    /// Projects each item through <paramref name="operation"/> concurrently, at most <paramref name="maxDegreeOfParallelism"/>
    /// at a time, and returns the results aligned to the input order. If any operation throws, all are awaited and the
    /// failures are surfaced together as an <see cref="AggregateException"/>.
    /// </summary>
    public static async Task<IReadOnlyList<TResult>> RunAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        int maxDegreeOfParallelism,
        Func<TItem, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(operation);

        if (items.Count == 0)
        {
            return [];
        }

        var degree = Math.Max(1, maxDegreeOfParallelism);
        var results = new TResult[items.Count];
        using var gate = new SemaphoreSlim(degree, degree);

        var tasks = new List<Task>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var item = items[index];

            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    results[index] = await operation(item, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        return results;
    }
}
