using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using FHIRBridge.LicenseServer.Licensing;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Pages;

public sealed class IndexModel : PageModel
{
    private readonly LicenseServerDbContext _db;

    public IndexModel(LicenseServerDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<Installation> Installations { get; private set; } = Array.Empty<Installation>();

    public int PendingLicenseRequestCount { get; private set; }

    public async Task OnGetAsync()
    {
        Installations = await _db.Installations
            .Include(x => x.CurrentIssuedLicense)
            .OrderByDescending(x => x.LastSeenUtc)
            .ToListAsync();

        PendingLicenseRequestCount = await _db.LicenseRequests.CountAsync(r => r.FulfilledAtUtc == null);
    }

    /// <summary>Renders one quota dimension as "used / limit" (or "used / Unlimited" when the license's
    /// value is the <see cref="LicenseFields.Unlimited"/> sentinel), or just the raw used count with a
    /// clear label when no license is linked so there's nothing to compare against.</summary>
    public static string FormatQuota(long used, int? limit)
    {
        if (limit is null)
        {
            return used.ToString();
        }

        return limit == LicenseFields.Unlimited ? $"{used} / Unlimited" : $"{used} / {limit}";
    }

    /// <summary>CSS status class for a stat chip: "none" (no license linked, or that dimension is
    /// unlimited — nothing to warn about), "ok" (comfortably under the cap), "warn" (80%+ of the cap),
    /// or "over" (at or past the cap).</summary>
    public static string QuotaStatusClass(long used, int? limit)
    {
        if (limit is null || limit == LicenseFields.Unlimited || limit == 0)
        {
            return "none";
        }

        var ratio = (double)used / limit.Value;
        return ratio switch
        {
            >= 1.0 => "over",
            >= 0.8 => "warn",
            _ => "ok",
        };
    }
}
