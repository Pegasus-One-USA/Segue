using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Branding;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public sealed partial class BrandConfigurationService : IBrandConfigurationService
{
    private static readonly string[] ValidThemeModes = ["light", "dark", "system"];
    private static readonly string[] ValidLoaderStyles = ["bar", "spinner", "list", "none"];

    private readonly IBrandConfigurationRepository _repository;

    public BrandConfigurationService(IBrandConfigurationRepository repository)
    {
        _repository = repository;
    }

    // tenantId is ALWAYS resolved server-side by the caller (BrandingController) — from the authenticated
    // user's own TenantId via ICurrentTenantResolver for the normal path, or from a validated Tenant.Code
    // lookup for the anonymous pre-login path. Never accepted from request body/client input here, which
    // is what makes "Tenant A sends Tenant B's id" structurally impossible rather than merely rejected.
    public async Task<BrandConfigurationDto> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var configuration = await _repository.GetByTenantIdAsync(tenantId, cancellationToken);
        return ToDto(configuration);
    }

    public async Task<BrandConfigurationDto> UpdateAsync(
        Guid tenantId,
        UpdateBrandConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);

        var configuration = await _repository.GetByTenantIdAsync(tenantId, cancellationToken);
        if (configuration is null)
        {
            configuration = new BrandConfiguration(
                tenantId,
                request.CompanyName, request.PrimaryColor, request.SecondaryColor, request.AccentColor,
                request.BackgroundColor, request.FontFamily, request.FooterText, request.SupportEmail,
                request.SupportPhone, request.Website, request.EmailFooterText, request.DefaultThemeMode,
                request.LoaderStyle, request.Assets.LogoUrl, request.Assets.DarkLogoUrl, request.Assets.FaviconUrl,
                request.Assets.LoginBackgroundUrl, request.Assets.LoginIllustrationUrl, request.Assets.EmailLogoUrl);
        }
        else
        {
            configuration.Update(
                request.CompanyName, request.PrimaryColor, request.SecondaryColor, request.AccentColor,
                request.BackgroundColor, request.FontFamily, request.FooterText, request.SupportEmail,
                request.SupportPhone, request.Website, request.EmailFooterText, request.DefaultThemeMode,
                request.LoaderStyle, request.Assets.LogoUrl, request.Assets.DarkLogoUrl, request.Assets.FaviconUrl,
                request.Assets.LoginBackgroundUrl, request.Assets.LoginIllustrationUrl, request.Assets.EmailLogoUrl);
        }

        await _repository.SaveAsync(configuration, cancellationToken);

        return ToDto(configuration);
    }

    // Mirrors the portal's own client-side rules exactly (branding-settings.component.ts's HEX_COLOR_PATTERN
    // and Validators.required/email) — the server is the authoritative check; the client-side copy exists
    // only to reject obviously-bad input before a round trip, same convention as password-policy validation.
    private static void Validate(UpdateBrandConfigurationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CompanyName))
        {
            throw new InvalidOperationException("Company name is required.");
        }

        foreach (var (label, value) in new[]
                 {
                     ("Primary color", request.PrimaryColor),
                     ("Secondary color", request.SecondaryColor),
                     ("Accent color", request.AccentColor),
                     ("Background color", request.BackgroundColor),
                 })
        {
            if (!HexColorPattern().IsMatch(value ?? string.Empty))
            {
                throw new InvalidOperationException($"{label} must be a valid hex color (e.g. #00A89D).");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.SupportEmail) && !EmailPattern().IsMatch(request.SupportEmail))
        {
            throw new InvalidOperationException("Support email must be a valid email address.");
        }

        if (!ValidThemeModes.Contains(request.DefaultThemeMode))
        {
            throw new InvalidOperationException("Default theme mode must be one of: light, dark, system.");
        }

        if (!ValidLoaderStyles.Contains(request.LoaderStyle))
        {
            throw new InvalidOperationException("Loader style must be one of: bar, spinner, list, none.");
        }
    }

    private static BrandConfigurationDto ToDto(BrandConfiguration? configuration) =>
        configuration is null
            ? BrandConfigurationDto.BuiltInDefault
            : new BrandConfigurationDto(
                configuration.CompanyName, configuration.PrimaryColor, configuration.SecondaryColor,
                configuration.AccentColor, configuration.BackgroundColor, configuration.FontFamily,
                configuration.FooterText, configuration.SupportEmail, configuration.SupportPhone,
                configuration.Website, configuration.EmailFooterText, configuration.DefaultThemeMode,
                configuration.LoaderStyle,
                new BrandAssetsDto(
                    configuration.LogoUrl, configuration.DarkLogoUrl, configuration.FaviconUrl,
                    configuration.LoginBackgroundUrl, configuration.LoginIllustrationUrl, configuration.EmailLogoUrl),
                configuration.ModifiedOnUtc ?? configuration.CreatedOnUtc, IsConfigured: true);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
