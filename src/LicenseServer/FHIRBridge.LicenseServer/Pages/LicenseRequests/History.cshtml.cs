using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Pages.LicenseRequests;

public sealed class HistoryModel : PageModel
{
    private readonly LicenseServerDbContext _db;

    public HistoryModel(LicenseServerDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<LicenseRequest> Requests { get; private set; } = Array.Empty<LicenseRequest>();

    public async Task OnGetAsync()
    {
        // Every request ever received, most-recently-active first — pending and fulfilled alike. The
        // action-focused /LicenseRequests page only shows what's still pending; this is the full record.
        Requests = await _db.LicenseRequests
            .OrderByDescending(r => r.LastSubmittedUtc)
            .ToListAsync();
    }
}
