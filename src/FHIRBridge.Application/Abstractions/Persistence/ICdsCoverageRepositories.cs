using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Access to <see cref="CdsPayer"/> rows (coverage requirements). Soft-deleted rows are never returned.</summary>
public interface ICdsPayerRepository
{
    /// <summary>All payers, by name.</summary>
    Task<IReadOnlyList<CdsPayer>> GetAllAsync(CancellationToken cancellationToken);

    Task<CdsPayer?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True when another live payer has this name (case-insensitive).</summary>
    Task<bool> NameExistsAsync(string name, Guid? excludeId, CancellationToken cancellationToken);

    Task AddAsync(CdsPayer payer, CancellationToken cancellationToken);

    Task UpdateAsync(CdsPayer payer, CancellationToken cancellationToken);

    /// <summary>Soft-deletes the payer and every rule of it, in one save.</summary>
    Task DeleteAsync(CdsPayer payer, IReadOnlyList<CdsCoverageRule> rules, CancellationToken cancellationToken);
}

/// <summary>Access to <see cref="CdsCoverageRule"/> rows. Soft-deleted rows are never returned.</summary>
public interface ICdsCoverageRuleRepository
{
    Task<IReadOnlyList<CdsCoverageRule>> GetAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CdsCoverageRule>> GetByPayerAsync(Guid payerId, CancellationToken cancellationToken);

    /// <summary>Ordered by system, code, effective from.</summary>
    Task<PagedResult<CdsCoverageRule>> GetPagedAsync(CdsCoverageRuleFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<CdsCoverageRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True when another live rule of the payer has the same system, code and effective-from date.</summary>
    Task<bool> KeyExistsAsync(Guid payerId, string system, string code, DateOnly effectiveFrom, Guid? excludeId, CancellationToken cancellationToken);

    Task AddAsync(CdsCoverageRule rule, CancellationToken cancellationToken);

    Task UpdateAsync(CdsCoverageRule rule, CancellationToken cancellationToken);

    Task DeleteAsync(CdsCoverageRule rule, CancellationToken cancellationToken);

    /// <summary>
    /// Applies an import in one save: adds <paramref name="added"/>, keeps the changes already made to the tracked
    /// <paramref name="updated"/> rows, and soft-deletes <paramref name="deleted"/>.
    /// </summary>
    Task ApplyAsync(
        IReadOnlyList<CdsCoverageRule> added,
        IReadOnlyList<CdsCoverageRule> updated,
        IReadOnlyList<CdsCoverageRule> deleted,
        CancellationToken cancellationToken);
}

/// <summary>Filters of the rules list; a null member is not applied.</summary>
/// <param name="Code">Code starts with this value (case-insensitive).</param>
/// <param name="Search">Text found in the system, code or reason.</param>
public sealed record CdsCoverageRuleFilter(
    Guid? PayerId = null,
    string? Code = null,
    string? Search = null,
    bool? IsEnabled = null);
