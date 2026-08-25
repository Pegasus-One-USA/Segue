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
    public async Task<ConnectionTestResultDto> TestConnectionAsync(
        MongoConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return new ConnectionTestResultDto(false, "Connection string is required.");
        }

        MongoUrl url;
        try
        {
            url = new MongoUrl(request.ConnectionString);
        }
        catch (Exception exception)
        {
            return new ConnectionTestResultDto(false, $"Invalid connection string: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(url.DatabaseName))
        {
            return new ConnectionTestResultDto(false, "The Mongo connection string must include a database name.");
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

            return new ConnectionTestResultDto(true, null);
        }
        catch (Exception exception)
        {
            return new ConnectionTestResultDto(false, exception.Message);
        }
    }
}
