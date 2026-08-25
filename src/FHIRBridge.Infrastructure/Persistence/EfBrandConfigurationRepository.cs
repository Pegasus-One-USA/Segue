using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IBrandConfigurationRepository"/>. There is at most one row per
/// tenant — SaveAsync inserts it on first save (for that tenant) and updates it on every save after.
/// </summary>
public sealed class EfBrandConfigurationRepository : IBrandConfigurationRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfBrandConfigurationRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    public Task<BrandConfiguration?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _db.BrandConfigurations.FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);

    public async Task SaveAsync(BrandConfiguration configuration, CancellationToken cancellationToken)
    {
        var existing = await _db.BrandConfigurations
            .FirstOrDefaultAsync(x => x.TenantId == configuration.TenantId, cancellationToken);

        if (existing is null)
        {
            _db.BrandConfigurations.Add(configuration);
        }
        else if (!ReferenceEquals(existing, configuration))
        {
            existing.Update(
                configuration.CompanyName, configuration.PrimaryColor, configuration.SecondaryColor,
                configuration.AccentColor, configuration.BackgroundColor, configuration.FontFamily,
                configuration.FooterText, configuration.SupportEmail, configuration.SupportPhone,
                configuration.Website, configuration.EmailFooterText, configuration.DefaultThemeMode,
                configuration.LoaderStyle, configuration.LogoUrl, configuration.DarkLogoUrl,
                configuration.FaviconUrl, configuration.LoginBackgroundUrl, configuration.LoginIllustrationUrl,
                configuration.EmailLogoUrl);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
