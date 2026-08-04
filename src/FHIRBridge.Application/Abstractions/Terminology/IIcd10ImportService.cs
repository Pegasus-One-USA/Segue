namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record Icd10ImportResult(string Version, int ImportedCodeCount);

public sealed record Icd10ImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedCodeCount, string Status, string? ErrorMessage);

public interface IIcd10ImportService
{
    Task<Icd10ImportResult> ImportAsync(Stream releaseZipStream, CancellationToken cancellationToken);
}
