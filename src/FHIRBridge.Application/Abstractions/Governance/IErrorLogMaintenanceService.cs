using FHIRBridge.Governance;

namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>How much the error log holds. <see cref="SizeBytes"/> is null when the database cannot report it.</summary>
/// <param name="SizeBytes">Space the database has allocated for the table. Databases keep this reserved after deletes
/// (it is reused for new entries), so it often does not shrink.</param>
/// <param name="DataBytes">Approximate size of the entries actually stored (their text columns plus a fixed per-row
/// allowance). This is the figure that goes down when entries are deleted.</param>
public sealed record ErrorLogStorageDto(long EntryCount, long? SizeBytes, DateTime? OldestUtc, DateTime? NewestUtc, long? DataBytes = null);

/// <summary>The error-log settings plus the current storage figures - what the settings screen loads in one call.</summary>
public sealed record ErrorLogSettingsResponse(
    ErrorLogSettings Settings,
    ErrorLogStorageDto Storage,
    IReadOnlyList<string> AvailableSeverities,
    IReadOnlyList<string> AvailableCategories,
    int MinimumRetentionDays);

/// <summary>Reads / writes the admin-controlled <see cref="ErrorLogSettings"/>.</summary>
public interface IErrorLogSettingsStore
{
    Task<ErrorLogSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(ErrorLogSettings settings, CancellationToken cancellationToken);
}

/// <summary>Outcome of a permanent delete. <see cref="SpaceReclaimed"/> is true when the database was also told to give
/// the freed space back (truncate / rebuild); <see cref="Note"/> explains when it could not.</summary>
public sealed record ErrorLogDeleteResult(int Deleted, bool SpaceReclaimed, string? Note);

/// <summary>Size reporting and age-based deletion for the error log.</summary>
public interface IErrorLogMaintenanceService
{
    Task<ErrorLogStorageDto> GetStorageAsync(CancellationToken cancellationToken);

    /// <summary>Deletes every error-log entry recorded before <paramref name="cutoffUtc"/>, together with the triage
    /// (Open/Resolved) rows that belonged only to them. Returns how many entries were deleted.</summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken);

    /// <summary>Permanently deletes error-log entries - everything when <paramref name="olderThanUtc"/> is null, otherwise
    /// those recorded before it - and then asks the database to release the space they used.</summary>
    Task<ErrorLogDeleteResult> DeleteAndReclaimAsync(DateTime? olderThanUtc, CancellationToken cancellationToken);
}
