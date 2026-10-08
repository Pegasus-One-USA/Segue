using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Which parts of an update change the write-side settings of an EHR write connection, and so need the EHR Write-Back
/// edit right on top of the vendor's own edit right. A connection that cannot write has no write side, so nothing here
/// applies (turning write access on is checked separately, against the requested Access).
/// </summary>
public static class SourceConnectionWriteSide
{
    /// <summary>
    /// True when <paramref name="request"/> would change a write-side setting of <paramref name="saved"/>, a
    /// connection that can write today: its Access (including dropping write, which also clears activation and the
    /// department), its department, or its vendor write-API activation (switching it off sends every write back to
    /// dry run). Only the Destination Connections form sends these; a source form sends null for all three, which
    /// keeps the saved values, so a re-save from Source Connections or a canvas source node never needs the right.
    /// The connection's endpoint and credentials stay under the vendor's own edit right, as for any connection.
    /// </summary>
    public static bool UpdateChangesWriteSide(SourceConnectionDto saved, CreateSourceConnectionRequest request)
    {
        if (!saved.Access.AllowsWrite())
        {
            return false;
        }

        return (request.Access is { } access && access != saved.Access)
               || (request.DepartmentId is { } departmentId && !SameText(departmentId, saved.DepartmentId))
               || (request.VendorWriteApisActivated is { } activated && activated != saved.VendorWriteApisActivated);
    }

    private static bool SameText(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
