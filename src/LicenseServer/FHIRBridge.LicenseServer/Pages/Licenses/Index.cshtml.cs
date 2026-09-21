using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Pages.Licenses;

public sealed class IndexModel : PageModel
{
    private readonly LicenseServerDbContext _db;

    public IndexModel(LicenseServerDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<IssuedLicense> Licenses { get; private set; } = Array.Empty<IssuedLicense>();

    public async Task OnGetAsync()
    {
        Licenses = await _db.IssuedLicenses
            .OrderByDescending(x => x.IssuedAtUtc)
            .ToListAsync();
    }
}
