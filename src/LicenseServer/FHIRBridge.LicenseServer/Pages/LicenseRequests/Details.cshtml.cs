using System.ComponentModel.DataAnnotations;
using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Pages.LicenseRequests;

public sealed class DetailsModel : PageModel
{
    private readonly LicenseServerDbContext _db;

    public DetailsModel(LicenseServerDbContext db)
    {
        _db = db;
    }

    public LicenseRequest RequestRecord { get; private set; } = default!;

    public bool SaveSucceeded { get; private set; }

    [BindProperty]
    public Guid Id { get; set; }

    [BindProperty]
    [Required]
    public string ClientName { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string? CompanyName { get; set; }

    [BindProperty]
    public string? Address { get; set; }

    [BindProperty]
    [Required]
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Editable here too — the value an install's Host header captured isn't always right (a
    /// manually-pasted code has none at all, or an install behind a load balancer might report an internal
    /// host) — but this is corrected by an admin who knows the customer's real domain, never by re-deriving
    /// it automatically. <see cref="LicenseRequest.UniqueKey"/> is never exposed here for editing.</summary>
    [BindProperty]
    public string? RequestHost { get; set; }

    [BindProperty]
    public string? DenialReason { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var request = await LoadAsync(id);
        if (request is null)
        {
            return NotFound();
        }

        RequestRecord = request;
        PopulateFormFrom(request);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var request = await LoadAsync(Id);
        if (request is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            RequestRecord = request;
            return Page();
        }

        // UniqueKey is deliberately never touched here — it's what ties a minted license back to the
        // requesting install (see LicenseRequest's remarks), and editing contact details is exactly the
        // case where that binding must NOT change.
        request.ClientName = ClientName.Trim();
        request.Email = Email.Trim();
        request.CompanyName = string.IsNullOrWhiteSpace(CompanyName) ? null : CompanyName.Trim();
        request.Address = string.IsNullOrWhiteSpace(Address) ? null : Address.Trim();
        request.PhoneNumber = PhoneNumber.Trim();
        request.RequestHost = string.IsNullOrWhiteSpace(RequestHost) ? null : RequestHost.Trim();

        await _db.SaveChangesAsync();

        RequestRecord = request;
        SaveSucceeded = true;
        return Page();
    }

    public async Task<IActionResult> OnPostDenyAsync()
    {
        var request = await LoadAsync(Id);
        if (request is null)
        {
            return NotFound();
        }

        // Never touches FulfilledAtUtc/FulfilledIssuedLicenseId — a request can only ever be minted OR
        // denied at a given time, and this leaves no doubt this one was actively turned down rather than
        // just never gotten to.
        request.DeniedAtUtc = DateTime.UtcNow;
        request.DenialReason = string.IsNullOrWhiteSpace(DenialReason) ? null : DenialReason.Trim();

        await _db.SaveChangesAsync();

        return RedirectToPage(new { id = request.Id });
    }

    private async Task<LicenseRequest?> LoadAsync(Guid id) =>
        await _db.LicenseRequests
            .Include(r => r.FulfilledIssuedLicense)
            .FirstOrDefaultAsync(r => r.Id == id);

    private void PopulateFormFrom(LicenseRequest request)
    {
        Id = request.Id;
        ClientName = request.ClientName;
        Email = request.Email;
        CompanyName = request.CompanyName;
        Address = request.Address;
        PhoneNumber = request.PhoneNumber;
        RequestHost = request.RequestHost;
    }
}
