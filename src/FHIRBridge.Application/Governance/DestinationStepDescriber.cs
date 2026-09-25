namespace FHIRBridge.Application.Governance;

/// <summary>
/// Turns one logged destination stage into a plain-language description — "Connected to the destination" and
/// "Completed — 33 of 50 records written" rather than a Stage/Status/count triple the reader has to assemble
/// themselves.
/// <para>DERIVED at read time, deliberately not stored — the same choice, for the same reasons, as
/// <see cref="ApiRequestStepDescriber"/>: it costs nothing to recompute, it applies retroactively to every row
/// already written, and the wording can be improved later without a migration or a backfill. The inputs it needs
/// are all already on the row.</para>
/// </summary>
public static class DestinationStepDescriber
{
    public static string Describe(
        string? stage, string? status, int? recordCount, int? writtenCount, string? detail)
    {
        var isConnect = string.Equals(stage, "Connect", StringComparison.OrdinalIgnoreCase);

        return status switch
        {
            _ when Is(status, "Failed") && isConnect => $"Could not connect{Suffix(detail)}",
            _ when Is(status, "Failed") => $"Write failed{Suffix(detail)}",
            _ when isConnect => $"Connected{Suffix(detail)}",
            _ when Is(status, "NoData") => "Completed — nothing to write",
            _ when Is(status, "PartialSuccess") => $"Completed — {Written(recordCount, writtenCount)}",
            _ when Is(status, "Succeeded") => $"Completed — {Written(recordCount, writtenCount)}",
            _ => "Destination activity"
        };
    }

    /// <summary>
    /// "3 records written" when every record landed, "33 of 50 records written" when some did not — the second
    /// form is the one that matters, because a partial write is otherwise indistinguishable from a whole one at a
    /// glance. Falls back to a bare count when the totals aren't both known.
    /// </summary>
    private static string Written(int? recordCount, int? writtenCount) => (recordCount, writtenCount) switch
    {
        (null, null) => "written",
        (_, null) => $"{recordCount} record(s)",
        (null, _) => $"{writtenCount} record(s) written",
        var (total, written) when written == total => $"{written} record(s) written",
        var (total, written) => $"{written} of {total} record(s) written",
    };

    /// <summary>Appends the stage's technical detail (which half of a two-part connect, the driver's message)
    /// when there is one, so a Fabric trace reads "Could not connect — Warehouse SQL" rather than twice the
    /// same line.</summary>
    private static string Suffix(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : $" — {detail}";

    private static bool Is(string? status, string expected) =>
        string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
}
