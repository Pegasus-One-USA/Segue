namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc Cosmos DB in Fabric connection test — the destination form's Test Connection button, before anything
/// is saved.
///
/// <para>Carries the discrete fields the wizard collects rather than a connection string, because Cosmos DB in
/// Fabric has no account keys: it authenticates through Entra only, so there is nothing string-shaped to test
/// (contrast <see cref="MongoConnectionTestRequest"/>, whose connection string IS the whole credential).</para>
/// </summary>
/// <param name="DestinationId">Set when re-testing an ALREADY-SAVED destination without retyping its client
/// secret — the form never re-displays a stored secret, so a blank <paramref name="Secret"/> plus this id means
/// "resolve the saved one from the vault". Mirrors MongoConnectionTestRequest.DestinationId.</param>
public sealed record CosmosDbFabricConnectionTestRequest(
    string Endpoint,
    string Database,
    string? AuthMode = null,
    string? TenantId = null,
    string? ClientId = null,
    string? Secret = null,
    string? ManagedIdentityClientId = null,
    string? AuthorityHost = null,
    Guid? DestinationId = null);

/// <summary>
/// Result of a Cosmos DB connection test. Returns the database's real container names on success so the mapping
/// canvas can offer them instead of requiring one typed blind — the same role
/// <see cref="MongoConnectionTestResultDto.Collections"/> plays for Mongo.
///
/// <para>Connected with an empty <paramref name="Containers"/> is a legitimate answer, not a failure: a Cosmos
/// database with no containers is exactly what a new Fabric database looks like. The form says so rather than
/// showing an error, because the next step differs — reaching the database is the hard part.</para>
/// </summary>
public sealed record CosmosDbFabricConnectionTestResultDto(
    bool Connected,
    string? Error,
    IReadOnlyList<string>? Containers = null);
