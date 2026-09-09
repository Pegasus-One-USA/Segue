using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record EhrEndpointDto(
    Guid Id,
    SourceSystemType Vendor,
    string VendorEndpointId,
    string Name,
    string FhirBaseUrl,
    string FormatType,
    string Status,
    DateTime CreatedOnUtc,
    string? CreatedBy,
    DateTime? ModifiedOnUtc,
    string? ModifiedBy,
    EhrEndpointType EndpointType = EhrEndpointType.MyChart);

/// <summary>
/// One server-side page of the admin EHR Endpoints screen. Same shape and reasoning as
/// <c>WorkflowSummaryPageDto</c> and <c>WorkflowRunHistoryPageDto</c>: <see cref="AvailableVendors"/> is the
/// distinct set of vendors that actually have endpoint rows, so the Source filter offers only those instead of
/// every <see cref="SourceSystemType"/> the platform can talk to. It is computed over the UNFILTERED set, so
/// choosing a vendor doesn't shrink the list it was chosen from.
/// </summary>
public sealed record EhrEndpointPageDto(
    IReadOnlyList<EhrEndpointDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<SourceSystemType> AvailableVendors);
