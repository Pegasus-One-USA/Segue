using System.Runtime.CompilerServices;

namespace FHIRBridge.Governance;

/// <summary>
/// Remembers which exception instances already went through <see cref="IGlobalExceptionManager"/> so the ambient
/// capture service never records the same failure twice. Weak references only — it never keeps an exception alive.
/// </summary>
public static class CapturedExceptionRegistry
{
    private static readonly ConditionalWeakTable<Exception, object> Captured = new();
    private static readonly object Marker = new();

    /// <summary>Marks the exception and everything in its inner chain as captured.</summary>
    public static void Mark(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 20; depth++)
        {
            Captured.TryAdd(exception, Marker);
            if (exception is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions) Mark(inner);
            }

            exception = exception.InnerException;
        }
    }

    public static bool WasCaptured(Exception exception) => Captured.TryGetValue(exception, out _);
}
