namespace FHIRBridge.Domain.Enums;

/// <summary>
/// Whether an <see cref="Entities.EhrEndpoint"/> row is a vendor's shared/generic test sandbox (<see cref="Epic"/> /
/// <see cref="Ecw"/>) or a specific customer/hospital's own branded production instance (<see cref="MyChart"/>).
/// Independent of <see cref="Entities.EhrEndpoint.Vendor"/> (<see cref="SourceSystemType"/>), which says which
/// vendor's directory a row came from — this says whether the URL behind it is a vendor sandbox or a real deployed
/// instance, and (for the sandbox rows) which vendor's sandbox.
/// <para>
/// Provider Standalone launches against the vendor-sandbox rows (<see cref="Epic"/> and <see cref="Ecw"/> — see
/// OAuthController's public-standalone-url); Patient Standalone launches against the branded production rows
/// (<see cref="MyChart"/>). <see cref="Ecw"/> was added so an eClinicalWorks provider sandbox no longer has to be
/// mislabeled as <see cref="Epic"/>; it is treated identically to <see cref="Epic"/> for the Provider Standalone
/// audience.
/// </para>
/// </summary>
public enum EhrEndpointType
{
    MyChart = 0,
    Epic = 1,

    /// <summary>eClinicalWorks (Healow) vendor sandbox — a Provider Standalone sandbox row, same audience as
    /// <see cref="Epic"/>. Kept distinct from Epic only so the directory row reads as eCW rather than Epic.</summary>
    Ecw = 2,
}
