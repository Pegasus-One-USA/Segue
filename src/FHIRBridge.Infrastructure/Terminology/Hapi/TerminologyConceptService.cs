using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Search/pagination and manual add/edit/delete over one HAPI terminology system's locally stored codes
/// (TRM_CODESYSTEM/TRM_CODESYSTEM_VER/TRM_CONCEPT — see HapiLocalTerminologyWriter, which performs the
/// same three-table write for a full sync). A code added or edited here lands in the exact same
/// CodeSystem/version row a "Run Now" sync would use, so a manually-added ICD-10-CM code is
/// indistinguishable from a synced one on the next lookup.
/// </summary>
public sealed class TerminologyConceptService : ITerminologyConceptService
{
    private const string ManualVersionId = "manual";

    private readonly FHIRBridgeDbContext _dbContext;
    private readonly HapiTerminologySystemRegistry _registry;

    public TerminologyConceptService(FHIRBridgeDbContext dbContext, HapiTerminologySystemRegistry registry)
    {
        _dbContext = dbContext;
        _registry = registry;
    }

    public async Task<PagedResult<TerminologyConceptDto>> GetPagedAsync(
        string systemCode, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(systemCode);
        var versionPid = await FindCurrentVersionPidAsync(descriptor.CodeSystemUri, cancellationToken);
        if (versionPid is null)
        {
            return new PagedResult<TerminologyConceptDto>(Array.Empty<TerminologyConceptDto>(), 0, page, pageSize);
        }

        var query = _dbContext.TrmConcepts.Where(c => c.CodeSystemPid == versionPid.Value);

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var upperTerm = term.ToUpperInvariant();
            query = query.Where(c =>
                c.CodeVal.ToUpper().Contains(upperTerm) ||
                (c.Display != null && c.Display.ToUpper().Contains(upperTerm)) ||
                (c.ShortDescription != null && c.ShortDescription.ToUpper().Contains(upperTerm)) ||
                (c.LongDescription != null && c.LongDescription.ToUpper().Contains(upperTerm)) ||
                (c.LongCommonName != null && c.LongCommonName.ToUpper().Contains(upperTerm)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(c => c.CodeVal)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new TerminologyConceptDto(
                c.Pid, c.CodeVal, c.Display, c.ShortDescription, c.LongDescription, c.LongCommonName, c.IsActive))
            .ToListAsync(cancellationToken);

        return new PagedResult<TerminologyConceptDto>(items, totalCount, page, pageSize);
    }

    public async Task<TerminologyConceptDto> AddAsync(
        string systemCode, UpsertTerminologyConceptRequest request, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(systemCode);
        var code = ValidateCode(request.Code);
        var versionPid = await GetOrCreateCurrentVersionPidAsync(descriptor, cancellationToken);

        var duplicate = await _dbContext.TrmConcepts
            .AnyAsync(c => c.CodeSystemPid == versionPid && c.CodeVal == code, cancellationToken);
        if (duplicate)
        {
            throw new InvalidOperationException($"Code '{code}' already exists in {descriptor.DisplayName}.");
        }

        var concept = new TrmConcept(
            versionPid,
            code,
            request.Display?.Trim(),
            request.ShortDescription,
            request.LongDescription,
            request.LongCommonName,
            request.IsActive);
        _dbContext.TrmConcepts.Add(concept);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new TerminologyConceptDto(
            concept.Pid, concept.CodeVal, concept.Display,
            concept.ShortDescription, concept.LongDescription, concept.LongCommonName, concept.IsActive);
    }

    public async Task<TerminologyConceptDto> UpdateAsync(
        string systemCode, long pid, UpsertTerminologyConceptRequest request, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(systemCode);
        var code = ValidateCode(request.Code);
        var versionPid = await FindCurrentVersionPidAsync(descriptor.CodeSystemUri, cancellationToken);

        var concept = versionPid is null
            ? null
            : await _dbContext.TrmConcepts
                .SingleOrDefaultAsync(c => c.Pid == pid && c.CodeSystemPid == versionPid.Value, cancellationToken);
        if (concept is null)
        {
            throw new NotFoundException(nameof(TrmConcept), pid);
        }

        var duplicate = await _dbContext.TrmConcepts
            .AnyAsync(c => c.CodeSystemPid == versionPid!.Value && c.CodeVal == code && c.Pid != pid, cancellationToken);
        if (duplicate)
        {
            throw new InvalidOperationException($"Code '{code}' already exists in {descriptor.DisplayName}.");
        }

        concept.Update(code, request.Display);
        concept.UpdateDescriptions(
            request.ShortDescription, request.LongDescription, request.LongCommonName, request.IsActive);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new TerminologyConceptDto(
            concept.Pid, concept.CodeVal, concept.Display,
            concept.ShortDescription, concept.LongDescription, concept.LongCommonName, concept.IsActive);
    }

    public async Task DeleteAsync(string systemCode, long pid, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(systemCode);
        var versionPid = await FindCurrentVersionPidAsync(descriptor.CodeSystemUri, cancellationToken);

        var concept = versionPid is null
            ? null
            : await _dbContext.TrmConcepts
                .SingleOrDefaultAsync(c => c.Pid == pid && c.CodeSystemPid == versionPid.Value, cancellationToken);
        if (concept is null)
        {
            throw new NotFoundException(nameof(TrmConcept), pid);
        }

        _dbContext.TrmConcepts.Remove(concept);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<long?> FindCurrentVersionPidAsync(string codeSystemUri, CancellationToken cancellationToken)
    {
        var codeSystem = await _dbContext.TrmCodeSystems
            .SingleOrDefaultAsync(x => x.CodeSystemUri == codeSystemUri, cancellationToken);
        return codeSystem?.CurrentVersionPid;
    }

    /// <summary>Reuses the version a sync already created (so manually-added codes sit alongside
    /// synced ones), or creates the CodeSystem/version pair under a fixed "manual" version id if this
    /// system has never been synced yet.</summary>
    private async Task<long> GetOrCreateCurrentVersionPidAsync(
        HapiTerminologySystemDescriptor descriptor, CancellationToken cancellationToken)
    {
        var codeSystem = await _dbContext.TrmCodeSystems
            .SingleOrDefaultAsync(x => x.CodeSystemUri == descriptor.CodeSystemUri, cancellationToken);
        if (codeSystem is null)
        {
            codeSystem = new TrmCodeSystem(descriptor.CodeSystemUri, descriptor.DisplayName);
            _dbContext.TrmCodeSystems.Add(codeSystem);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (codeSystem.CurrentVersionPid is { } existingVersionPid)
        {
            return existingVersionPid;
        }

        var version = new TrmCodeSystemVer(codeSystem.Pid, ManualVersionId, descriptor.DisplayName);
        _dbContext.TrmCodeSystemVers.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);

        codeSystem.SetCurrentVersion(version.Pid);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return version.Pid;
    }

    private static string ValidateCode(string code)
    {
        var trimmed = code?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("Code is required.");
        }

        return trimmed;
    }
}
