using System.Threading;

namespace FHIRBridge.Governance;

/// <summary>
/// Generates globally unique, human-quotable error reference ids of the form
/// <c>ERR-yyyyMMdd-NNNNNN</c> (e.g. <c>ERR-20260721-000123</c>).
///
/// The 6-character suffix combines a per-process day-scoped counter with a base-36 encoding so ids are
/// readable and collision-resistant across instances; the unique index on <c>ErrorLogs.ErrorReferenceId</c>
/// is the ultimate backstop. Cloud-agnostic: no dependency on any cloud sequence/counter service.
/// </summary>
public static class ErrorReference
{
    private const string Prefix = "ERR";
    private static long _sequence;

    public static string New() => New(DateTime.UtcNow);

    public static string New(DateTime utcNow)
    {
        var next = Interlocked.Increment(ref _sequence);
        // 6 chars of base-36 keeps the reference short while giving ~2.1B distinct values before wraparound.
        var suffix = ToBase36(next).PadLeft(6, '0');
        if (suffix.Length > 6)
        {
            suffix = suffix[^6..];
        }

        return $"{Prefix}-{utcNow:yyyyMMdd}-{suffix}";
    }

    private static string ToBase36(long value)
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (value == 0)
        {
            return "0";
        }

        Span<char> buffer = stackalloc char[13];
        var index = buffer.Length;
        var remaining = value;
        while (remaining > 0)
        {
            buffer[--index] = alphabet[(int)(remaining % 36)];
            remaining /= 36;
        }

        return new string(buffer[index..]);
    }
}
