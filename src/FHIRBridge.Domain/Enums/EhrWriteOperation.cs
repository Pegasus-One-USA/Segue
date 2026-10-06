namespace FHIRBridge.Domain.Enums;

/// <summary>
/// A FHIR write interaction an EHR accepts for one resource type. Deliberately not <c>[Flags]</c>: a capability
/// carries a set of these and a ledger row carries exactly one, and the API's string enum converter would render a
/// flags combination as "Create, Update", which callers cannot parse back.
/// </summary>
public enum EhrWriteOperation
{
    Create = 1,
    Update = 2,
}
