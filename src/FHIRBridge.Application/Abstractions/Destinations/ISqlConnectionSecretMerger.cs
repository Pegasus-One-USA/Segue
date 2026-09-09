using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Splices a SQL-family destination's already-stored credentials into a freshly-built connection string that's
/// missing them — the workflow wizard forks a brand-new <see cref="Domain.Entities.DestinationConfiguration"/>
/// whenever the user edits ANY field on a picked existing connection (never mutates a connection other
/// workflows might share), including a field that has nothing to do with credentials (e.g. "Require SSL"). The
/// password is never re-populated into the form (secrets never come back from the API), so the fork's own
/// freshly-built connection string has a blank password unless the user retypes it — this fills that gap in
/// from the connection being forked from, so the user only has to retype credentials when actually changing
/// them, not when toggling an unrelated setting.
/// </summary>
public interface ISqlConnectionSecretMerger
{
    /// <summary>
    /// Returns <paramref name="newSecret"/> with credentials inherited from <paramref name="existingSecret"/>,
    /// or <c>null</c> when there's nothing to inherit (not a SQL-family type, <paramref name="newSecret"/>
    /// already has a password, <paramref name="existingSecret"/> has none, the new connection uses an
    /// authentication mode with no password concept, or either string fails to parse). Never throws — a
    /// malformed connection string on either side degrades to <c>null</c> rather than breaking the save.
    /// </summary>
    string? TryInheritCredentials(DestinationType destinationType, string existingSecret, string newSecret);
}
