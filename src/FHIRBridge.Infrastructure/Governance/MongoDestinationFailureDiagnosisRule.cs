using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Diagnoses MongoDB destination failures — both <c>MappedMongoDestinationWriter</c>'s own config-validation
/// throws (e.g. missing database/collection name) and the MongoDB.Driver's connection/auth exceptions, matched by
/// type name rather than a direct package reference so this rule lives in FHIRBridge.Infrastructure without
/// pulling in the Mongo driver just for exception types.
/// </summary>
public sealed class MongoDestinationFailureDiagnosisRule : IFailureDiagnosisRule
{
    public bool Matches(Exception exception) =>
        exception.GetType().Namespace?.Contains("MongoDB", StringComparison.OrdinalIgnoreCase) == true
        || exception.Message.Contains("Mongo connection string", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("name a collection", StringComparison.OrdinalIgnoreCase);

    public Diagnosis Diagnose(Exception exception) => exception.GetType().Name switch
    {
        "MongoAuthenticationException" => new Diagnosis(
            "MongoDB rejected the credentials in this destination's connection string.",
            DiagnosisAction.SelfFix),
        "MongoConnectionException" or "MongoNotPrimaryException" or "TimeoutException" => new Diagnosis(
            "Could not reach the MongoDB server — check the connection string's host/port and network access " +
            "from this environment.",
            DiagnosisAction.SelfFix),
        _ when exception.Message.Contains("Mongo connection string", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("name a collection", StringComparison.OrdinalIgnoreCase) => new Diagnosis(
                "This destination's Mongo connection string or collection name is not fully configured — check " +
                "the destination configuration.",
                DiagnosisAction.SelfFix),
        _ => new Diagnosis(
            "MongoDB rejected or could not complete this write — check the connection string, database, and " +
            "collection configured on this destination.",
            DiagnosisAction.SelfFix),
    };
}
