using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Tests an ad-hoc MongoDB connection before anything is saved: parses the connection string (which must embed a
/// database name, same as <see cref="MappedMongoDestinationWriter"/> requires), opens a client with a short
/// server-selection timeout so a bad host fails fast rather than after the driver's ~30s default, and runs a
/// <c>{ ping: 1 }</c> command against the database. Never throws for connection failures; returns
/// <c>Connected=false</c> + <c>Error</c> instead.
/// </summary>
public sealed class MongoDestinationConnectionTestService : IMongoDestinationConnectionTestService
{
    public async Task<MongoConnectionTestResultDto> TestConnectionAsync(
        MongoConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return new MongoConnectionTestResultDto(false, "Connection string is required.");
        }

        MongoUrl url;
        try
        {
            url = new MongoUrl(request.ConnectionString);
        }
        catch (Exception exception)
        {
            return new MongoConnectionTestResultDto(false, $"Invalid connection string: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(url.DatabaseName))
        {
            return new MongoConnectionTestResultDto(false, "The Mongo connection string must include a database name.");
        }

        try
        {
            var settings = MongoClientSettings.FromUrl(url);
            // Fail fast instead of the driver's default ~30s server-selection wait when the host is unreachable.
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
            settings.ConnectTimeout = TimeSpan.FromSeconds(8);

            var client = new MongoClient(settings);
            var database = client.GetDatabase(url.DatabaseName);
            await database.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1), cancellationToken: cancellationToken);

            // The full collection list is returned on every successful connect (not just when Collection is
            // supplied) so the form can offer real names as an autocomplete instead of requiring one typed blind.
            var collections = await (await database.ListCollectionNamesAsync(
                cancellationToken: cancellationToken)).ToListAsync(cancellationToken);

            // Collection existence is checked here too (not just at pipeline-run time in
            // MappedMongoDestinationWriter) so a typo'd/never-created collection surfaces on the form
            // immediately, unless the caller has opted into auto-create via the checkbox.
            if (!string.IsNullOrWhiteSpace(request.Collection) && !request.CreateIfNotExists)
            {
                var collectionName = MongoCollectionNameResolver.Resolve(request.Collection);
                if (!collections.Contains(collectionName, StringComparer.Ordinal))
                {
                    return new MongoConnectionTestResultDto(
                        false,
                        $"Connected, but collection '{collectionName}' does not exist. Create it in your database, or enable \"Create collection if not exists\".",
                        collections);
                }
            }

            return new MongoConnectionTestResultDto(true, null, collections);
        }
        catch (Exception exception)
        {
            return new MongoConnectionTestResultDto(false, exception.Message);
        }
    }
}
