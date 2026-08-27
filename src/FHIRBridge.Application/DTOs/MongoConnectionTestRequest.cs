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
public sealed record MongoConnectionTestRequest(
    string ConnectionString,
    string? Collection = null,
    bool CreateIfNotExists = false);
