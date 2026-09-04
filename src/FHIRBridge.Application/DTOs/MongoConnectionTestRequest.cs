namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc MongoDB connection test (the destination form's Test Connection button, before anything is saved).
/// The connection string is the whole credential — Mongo has no split server/database/credentials form — and
/// must embed the database name (e.g. <c>mongodb://user:pass@host:27017/dbname?authSource=admin</c>), same shape
/// <see cref="Domain.Entities.DestinationConfiguration"/>'s secret carries for a saved Mongo destination. The
/// result reuses the shared <see cref="ConnectionTestResultDto"/> (Connected + Error).
/// </summary>
/// <param name="Collection">Optional — when supplied, the test also checks whether this collection exists (same
/// name-normalization <see cref="Domain.Entities.DestinationConfiguration.Target"/> gets at write time), so a
/// missing collection surfaces here instead of only at pipeline-run time.</param>
/// <param name="CreateIfNotExists">Mirrors the form's "Create collection if not exists" checkbox — when true, a
/// missing <paramref name="Collection"/> doesn't fail the test (the write path will create it).</param>
/// <param name="DestinationId">When set and <paramref name="ConnectionString"/> is blank, the connection string
/// is resolved server-side from this already-saved destination's stored secret instead — lets the wizard verify
/// an existing connection without the browser ever holding or resending it. Ignored when
/// <paramref name="ConnectionString"/> is non-blank (a genuinely new/changed value always wins).</param>
public sealed record MongoConnectionTestRequest(
    string ConnectionString,
    string? Collection = null,
    bool CreateIfNotExists = false,
    Guid? DestinationId = null);

/// <summary>
/// Result of a Mongo connection test — extends the shared <see cref="ConnectionTestResultDto"/> shape with the
/// database's real collection names on success, so the destination form can offer them as an autocomplete
/// instead of requiring the collection name to be typed blind (a typo there only surfaced previously at
/// pipeline-run time, or now as a Test Connection failure if it doesn't already exist).
/// </summary>
public sealed record MongoConnectionTestResultDto(
    bool Connected,
    string? Error,
    IReadOnlyList<string>? Collections = null);
