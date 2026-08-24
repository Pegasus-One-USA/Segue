namespace FHIRBridge.Application.DTOs;

/// <summary>Mirrors the portal's BrandAssets interface exactly (see brand-configuration.model.ts) so the
/// frontend can assign the response straight onto its own model with no field-by-field remapping.</summary>
public sealed record BrandAssetsDto(
    string LogoUrl,
    string DarkLogoUrl,
    string FaviconUrl,
    string LoginBackgroundUrl,
    string LoginIllustrationUrl,
    string EmailLogoUrl);

/// <summary>
/// One tenant's white-label branding configuration, returned by GET/PUT /api/v1/branding. Mirrors the
/// portal's BrandConfiguration interface field-for-field, minus `tenantId` — the portal never needs to
/// know the tenant's raw id for this purpose, only its own resolved branding.
/// </summary>
public sealed record BrandConfigurationDto(
    string CompanyName,
    string PrimaryColor,
    string SecondaryColor,
    string AccentColor,
    string BackgroundColor,
    string FontFamily,
    string FooterText,
    string SupportEmail,
    string SupportPhone,
    string Website,
    string EmailFooterText,
    string DefaultThemeMode,
    string LoaderStyle,
    BrandAssetsDto Assets,
    DateTime UpdatedOnUtc,
    // False when this tenant has never saved a branding row and this is the built-in fallback — lets the
    // portal distinguish "no custom branding configured" from "an admin genuinely chose these exact
    // default-looking values."
    bool IsConfigured)
{
    /// <summary>The built-in fallback shape — used both by BrandConfigurationService.ToDto's null branch
    /// (a real tenant with no saved row yet) and by BrandingController when NO tenant could be resolved at
    /// all (anonymous request, no/invalid ?tenant=code) — there is deliberately no "tenant" to look up in
    /// that second case, so this is the one placed that constructs the fallback without a repository call.</summary>
    public static BrandConfigurationDto BuiltInDefault { get; } = new(
        "Segue", "#00A89D", "#0076A8", "#007A72", "#F5F7FA", string.Empty, "Segue Platform",
        string.Empty, string.Empty, string.Empty, string.Empty, "light", "list",
        new BrandAssetsDto(string.Empty, string.Empty, "favicon.ico", string.Empty, string.Empty, string.Empty),
        DateTime.MinValue, IsConfigured: false);
}
