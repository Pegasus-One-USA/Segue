using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services.Terminology;

/// <summary>Search, pagination, and manual add/edit/delete over one HAPI terminology system's locally
/// stored codes (TRM_CODESYSTEM/TRM_CODESYSTEM_VER/TRM_CONCEPT) — backs Settings → General →
/// Terminology's "View All Codes" screen. Implementation lives in Infrastructure since it resolves
/// each short registry code (e.g. "Icd10") to its CodeSystemUri via HapiTerminologySystemRegistry.</summary>
public interface ITerminologyConceptService
{
    Task<PagedResult<TerminologyConceptDto>> GetPagedAsync(
        string systemCode, string? search, int page, int pageSize, CancellationToken cancellationToken);

    Task<TerminologyConceptDto> AddAsync(
        string systemCode, UpsertTerminologyConceptRequest request, CancellationToken cancellationToken);

    Task<TerminologyConceptDto> UpdateAsync(
        string systemCode, long pid, UpsertTerminologyConceptRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(string systemCode, long pid, CancellationToken cancellationToken);
}
