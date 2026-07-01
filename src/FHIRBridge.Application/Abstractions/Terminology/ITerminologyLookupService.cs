using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Terminology;

public interface ITerminologyLookupService
{
    Task<TerminologyLookupResult?> LookupAsync(
        string system,
        string code,
        CancellationToken cancellationToken);
}
