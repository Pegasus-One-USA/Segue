namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// The one rule for which resource elements may be applied AFTER a mapped column's own Transformations, i.e. read
/// back from the pre-de-identification snapshot. Shared by the snapshot builder (SafeHarborDeIdentificationService,
/// which decides what to restore) and the Mapping node (which decides what to defer), so the two can never
/// disagree: a column is only ever deferred on an element the snapshot actually holds, and the snapshot only ever
/// holds an element no non-deferrable rule touched.
/// </summary>
public static class DeIdentificationDeferral
{
    /// <summary>
    /// Whether the element at <paramref name="sourceField"/> may be restored to its original value in the snapshot.
    /// True only when at least one applied redaction addresses exactly this element, and EVERY applied redaction
    /// whose element overlaps it — the element itself, any ancestor, or any descendant — is a deferrable strategy
    /// (<see cref="DeIdentificationStrategyNames.DeferrableAfterTransformations"/>) on exactly this element.
    ///
    /// Overlap rather than path equality is the point. Restoring "name" restores its whole original subtree, so a
    /// Hash on "name.family" or a rewritten "…reference" beneath it would be undone; restoring "name.given" under a
    /// Remove of all of "name" would bring back data that rule deleted. Either makes the element unrestorable, and
    /// the column falls back to redaction-first.
    /// </summary>
    public static bool CanRestore(
        string sourceField, IReadOnlyCollection<DeIdentificationFieldHop> appliedHops, string resourceType)
    {
        var target = DeIdentificationSourcePath.Segments(sourceField, resourceType);
        if (target.Length == 0)
        {
            return false;
        }

        var coversTarget = false;
        foreach (var hop in appliedHops)
        {
            if (!hop.Success)
            {
                continue;
            }

            var segments = DeIdentificationSourcePath.Segments(hop.SourceField, resourceType);
            if (!Overlaps(segments, target))
            {
                continue;
            }

            if (segments.Length != target.Length
                || !DeIdentificationStrategyNames.DeferrableAfterTransformations.Contains(hop.Strategy))
            {
                return false;
            }

            coversTarget = true;
        }

        return coversTarget;
    }

    /// <summary>True when one path is the other or lies beneath it ("name" and "name.family").</summary>
    private static bool Overlaps(string[] first, string[] second)
    {
        var shared = Math.Min(first.Length, second.Length);
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(first[i], second[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
