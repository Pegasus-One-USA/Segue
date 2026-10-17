using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.DTOs;

/// <param name="Access">What the connection may be used for. Null means Read on create and "keep what is saved" on
/// update: the workflow canvas rebuilds this whole request on every save, and older portal bundles and the workflow
/// copy path do not send it, so a missing value must never downgrade a write connection.</param>
/// <param name="VendorWriteApisActivated">The practice has the vendor's contracted / proprietary write APIs turned on
/// (see <c>SourceConnection.VendorWriteApisActivated</c>). Null keeps what is saved, false on create, for the same
/// reason as <paramref name="Access"/>.</param>
/// <param name="DepartmentId">athenaOne write connections: the department new patients are registered in AND the
/// department every write is filed under, existing patients included (in place of each patient's own primary
/// department), for every EHR Write-Back node over this connection that does not set its own
/// <c>dest_targetDepartmentId</c> (see <c>SourceConnection.DepartmentId</c>). Leave it empty to file existing patients'
/// writes under their own department. Null means none on create and "keep what is saved" on update; an empty value
/// clears it. Ignored (kept empty) for a connection without write access.</param>
public sealed record CreateSourceConnectionRequest(
    string Name,
    SourceSystemType SourceSystemType,
    string BaseUrl,
    SourceAuthenticationDto Authentication,
    ApplicationType? ApplicationType = null,
    SourceInteractiveConfigurationDto? Interactive = null,
    SourceRetrievalConfigurationDto? Retrieval = null,
    SourceConnectionAccess? Access = null,
    bool? VendorWriteApisActivated = null,
    string? DepartmentId = null);
