namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Read-side counterpart to <see cref="CurrentUserInfo.AuditName"/>: turns a stored provenance value
/// (CreatedBy/ModifiedBy/DeletedBy, or any other "who did this" string stamped via AuditName) back into a
/// display name for list/detail screens. A stored value is either an internal Users.Id GUID (the current
/// AuditName behavior) or an older/system string predating that (email, "anonymous", "system", a Worker
/// automation label) — the latter is returned unchanged since it's already human-readable.
/// </summary>
public interface IUserDisplayNameResolver
{
    /// <summary>Resolves every distinct, non-empty value in <paramref name="actorValues"/> to a display name.
    /// The returned dictionary is keyed by the original raw value, so callers can look up each row's own
    /// CreatedBy/ModifiedBy string without re-parsing it.</summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IEnumerable<string?> actorValues, CancellationToken cancellationToken);

    /// <summary>Convenience for a single value, e.g. one entity's CreatedBy.</summary>
    async Task<string?> ResolveOneAsync(string? actorValue, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorValue))
        {
            return actorValue;
        }

        var resolved = await ResolveAsync([actorValue], cancellationToken);
        return resolved.TryGetValue(actorValue, out var name) ? name : actorValue;
    }
}
