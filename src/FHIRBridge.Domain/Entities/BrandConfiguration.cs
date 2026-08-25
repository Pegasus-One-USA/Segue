using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// White-label branding configuration for one <see cref="Tenant"/> — at most one row per tenant (see the
/// unique index on TenantId in BrandConfigurationConfig). Was originally built as a single global,
/// non-tenant-scoped row (mirroring NotificationSettings) back when this codebase had no real Tenant
/// entity at all; now that Tenant exists, this is scoped by it like everything else user-facing.
/// </summary>
public sealed class BrandConfiguration : AuditableChildEntity<Guid>
{
    private BrandConfiguration()
    {
    }

    public BrandConfiguration(
        Guid tenantId,
        string companyName,
        string primaryColor,
        string secondaryColor,
        string accentColor,
        string backgroundColor,
        string fontFamily,
        string footerText,
        string supportEmail,
        string supportPhone,
        string website,
        string emailFooterText,
        string defaultThemeMode,
        string loaderStyle,
        string logoUrl,
        string darkLogoUrl,
        string faviconUrl,
        string loginBackgroundUrl,
        string loginIllustrationUrl,
        string emailLogoUrl)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        CompanyName = companyName;
        PrimaryColor = primaryColor;
        SecondaryColor = secondaryColor;
        AccentColor = accentColor;
        BackgroundColor = backgroundColor;
        FontFamily = fontFamily;
        FooterText = footerText;
        SupportEmail = supportEmail;
        SupportPhone = supportPhone;
        Website = website;
        EmailFooterText = emailFooterText;
        DefaultThemeMode = defaultThemeMode;
        LoaderStyle = loaderStyle;
        LogoUrl = logoUrl;
        DarkLogoUrl = darkLogoUrl;
        FaviconUrl = faviconUrl;
        LoginBackgroundUrl = loginBackgroundUrl;
        LoginIllustrationUrl = loginIllustrationUrl;
        EmailLogoUrl = emailLogoUrl;
    }

    public Guid TenantId { get; private set; }

    public string CompanyName { get; private set; } = "Segue";
    public string PrimaryColor { get; private set; } = "#00A89D";
    public string SecondaryColor { get; private set; } = "#0076A8";
    public string AccentColor { get; private set; } = "#007A72";
    public string BackgroundColor { get; private set; } = "#F5F7FA";
    /// <summary>Optional — the portal falls back to its platform default font stack when empty.</summary>
    public string FontFamily { get; private set; } = string.Empty;
    public string FooterText { get; private set; } = "Segue Platform";
    public string SupportEmail { get; private set; } = string.Empty;
    public string SupportPhone { get; private set; } = string.Empty;
    public string Website { get; private set; } = string.Empty;
    public string EmailFooterText { get; private set; } = string.Empty;
    /// <summary>One of "light" / "dark" / "system" — validated by BrandConfigurationService, not a CLR enum,
    /// so the JSON wire shape matches the portal's own string-literal union exactly with no casing mapping.</summary>
    public string DefaultThemeMode { get; private set; } = "light";
    /// <summary>One of "bar" / "spinner" / "list" / "none" — same reasoning as <see cref="DefaultThemeMode"/>.</summary>
    public string LoaderStyle { get; private set; } = "list";

    // Asset fields: the portal encodes uploaded images as data: URIs client-side (no separate blob-storage
    // upload endpoint exists), so these can be tens to hundreds of KB — deliberately left with NO
    // HasMaxLength in BrandConfigurationConfig, which maps to SQL Server nvarchar(max).
    public string LogoUrl { get; private set; } = string.Empty;
    public string DarkLogoUrl { get; private set; } = string.Empty;
    public string FaviconUrl { get; private set; } = "favicon.ico";
    public string LoginBackgroundUrl { get; private set; } = string.Empty;
    public string LoginIllustrationUrl { get; private set; } = string.Empty;
    public string EmailLogoUrl { get; private set; } = string.Empty;

    public void Update(
        string companyName,
        string primaryColor,
        string secondaryColor,
        string accentColor,
        string backgroundColor,
        string fontFamily,
        string footerText,
        string supportEmail,
        string supportPhone,
        string website,
        string emailFooterText,
        string defaultThemeMode,
        string loaderStyle,
        string logoUrl,
        string darkLogoUrl,
        string faviconUrl,
        string loginBackgroundUrl,
        string loginIllustrationUrl,
        string emailLogoUrl)
    {
        CompanyName = companyName;
        PrimaryColor = primaryColor;
        SecondaryColor = secondaryColor;
        AccentColor = accentColor;
        BackgroundColor = backgroundColor;
        FontFamily = fontFamily;
        FooterText = footerText;
        SupportEmail = supportEmail;
        SupportPhone = supportPhone;
        Website = website;
        EmailFooterText = emailFooterText;
        DefaultThemeMode = defaultThemeMode;
        LoaderStyle = loaderStyle;
        LogoUrl = logoUrl;
        DarkLogoUrl = darkLogoUrl;
        FaviconUrl = faviconUrl;
        LoginBackgroundUrl = loginBackgroundUrl;
        LoginIllustrationUrl = loginIllustrationUrl;
        EmailLogoUrl = emailLogoUrl;
    }
}
