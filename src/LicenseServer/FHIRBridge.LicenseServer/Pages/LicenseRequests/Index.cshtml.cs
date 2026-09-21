using FHIRBridge.LicenseServer.Api;
using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using FHIRBridge.LicenseServer.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Pages.LicenseRequests;

public sealed class IndexModel : PageModel
{
    private readonly LicenseServerDbContext _db;

    public IndexModel(LicenseServerDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<LicenseRequest> Requests { get; private set; } = Array.Empty<LicenseRequest>();

    /// <summary>Pasted in from the "Encrypted Data" a customer shares with support when their install's
    /// direct <c>POST /api/license-requests</c> call couldn't reach this server (see the main repo's
    /// License Request screen's Failed state).</summary>
    [BindProperty]
    public string? EncodedPayload { get; set; }

    public string? DecodeErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadRequestsAsync();
    }

    public async Task<IActionResult> OnPostDecodeAsync()
    {
        var payload = string.IsNullOrWhiteSpace(EncodedPayload)
            ? null
            : LicenseRequestPayloadEncoder.TryDecode(EncodedPayload);

        if (payload is null)
        {
            DecodeErrorMessage = "Couldn't decode that code — check it was copied in full and try again.";
            await LoadRequestsAsync();
            return Page();
        }

        await LicenseRequestEndpoints.UpsertLicenseRequestAsync(
            _db,
            new LicenseRequestIntakeBody(
                payload.ClientName, payload.Email, payload.CompanyName, payload.Address, payload.PhoneNumber,
                payload.UniqueKey, payload.RequestHost),
            receivedManually: true,
            CancellationToken.None);

        return RedirectToPage();
    }

    private async Task LoadRequestsAsync()
    {
        // Only what still needs action — a fulfilled or denied request is decided and belongs on the full
        // /LicenseRequests/History list instead of competing for space here.
        Requests = await _db.LicenseRequests
            .Where(r => r.FulfilledAtUtc == null && r.DeniedAtUtc == null)
            .OrderByDescending(r => r.LastSubmittedUtc)
            .ToListAsync();
    }
}
