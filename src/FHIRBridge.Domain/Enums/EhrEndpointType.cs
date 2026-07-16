namespace FHIRBridge.Domain.Enums;

/// <summary>
/// Whether an <see cref="Entities.EhrEndpoint"/> row is a vendor's shared/generic test sandbox ("Epic") or a
/// specific customer/hospital's own branded production instance ("MyChart"). Independent of
/// <see cref="Entities.EhrEndpoint.Vendor"/> (<see cref="SourceSystemType"/>), which says which vendor's directory
/// a row came from — this says whether the URL behind it is the vendor's own sandbox or a real deployed instance.
/// </summary>
public enum EhrEndpointType
{
    MyChart = 0,
    Epic = 1
}
