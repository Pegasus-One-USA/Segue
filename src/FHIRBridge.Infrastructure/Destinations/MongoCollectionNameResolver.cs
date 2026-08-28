using FHIRBridge.Application.Mappings;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Normalizes a stored destination object (or an ad-hoc collection name typed into the Mongo destination form)
/// into the bare collection name Mongo needs — shared by <see cref="MappedMongoDestinationWriter"/> (write time)
/// and <see cref="MongoDestinationConnectionTestService"/> (Test Connection), so the two can never disagree about
/// which collection a given value resolves to.
/// </summary>
internal static class MongoCollectionNameResolver
{
    public static string Resolve(string destinationObject)
    {
        // A stored destination object may carry a ';mode=<writeMode>' (or legacy '?key=') suffix — neither is
        // part of the collection identifier. DestinationObjectParser strips both the same way every relational
        // writer does (RelationalDestinationWriterBase.ParseTarget).
        var name = DestinationObjectParser.ParseTableName(destinationObject);

        // Mongo has no schema layer distinct from the database (same convention as MySQL) — a "Schema.Collection"
        // style destination object only ever needs the last segment.
        var dotIndex = name.LastIndexOf('.');
        if (dotIndex >= 0)
        {
            name = name[(dotIndex + 1)..];
        }

        return name;
    }
}
