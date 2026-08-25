namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc MongoDB connection test (the destination form's Test Connection button, before anything is saved).
/// The connection string is the whole credential — Mongo has no split server/database/credentials form — and
/// must embed the database name (e.g. <c>mongodb://user:pass@host:27017/dbname?authSource=admin</c>), same shape
/// <see cref="Domain.Entities.DestinationConfiguration"/>'s secret carries for a saved Mongo destination. The
/// result reuses the shared <see cref="ConnectionTestResultDto"/> (Connected + Error).
/// </summary>
public sealed record MongoConnectionTestRequest(string ConnectionString);
