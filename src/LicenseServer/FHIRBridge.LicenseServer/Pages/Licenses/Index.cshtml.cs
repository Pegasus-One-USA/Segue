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

    /// <summary>Issued-license id → the License Request it was minted against, for the "View License
    /// Request" link — <see cref="LicenseRequest"/> only points forward (its own
    /// <c>FulfilledIssuedLicenseId</c>), so this is the reverse lookup. A license minted without a linked
    /// request (or one minted before this feature existed) simply has no entry here.</summary>
    public IReadOnlyDictionary<Guid, Guid> RequestIdByLicenseId { get; private set; } =
        new Dictionary<Guid, Guid>();

    public async Task OnGetAsync()
    {
        Licenses = await _db.IssuedLicenses
            .OrderByDescending(x => x.IssuedAtUtc)
            .ToListAsync();

        RequestIdByLicenseId = await _db.LicenseRequests
            .Where(r => r.FulfilledIssuedLicenseId != null)
            .ToDictionaryAsync(r => r.FulfilledIssuedLicenseId!.Value, r => r.Id);
    }
}
