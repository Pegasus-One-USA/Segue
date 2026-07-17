namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// A fully-serialized, in-memory export produced by a destination writer, before delivery. Format-agnostic — a CSV
/// writer, and later an Excel/PDF/XML writer, all produce one of these and hand it to the same delivery strategies.
/// </summary>
public sealed record GeneratedFile(string FileName, string ContentType, byte[] Content);
