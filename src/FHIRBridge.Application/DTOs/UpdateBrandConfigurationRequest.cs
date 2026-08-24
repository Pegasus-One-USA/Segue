namespace FHIRBridge.Application.DTOs;

public sealed record UpdateBrandConfigurationRequest(
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
    BrandAssetsDto Assets);
