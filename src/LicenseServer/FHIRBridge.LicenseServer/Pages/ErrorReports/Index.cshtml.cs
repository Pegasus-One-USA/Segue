using System.ComponentModel.DataAnnotations;
using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using FHIRBridge.LicenseServer.ErrorReports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Pages.ErrorReports;

[RequestSizeLimit(MaxUploadBytes)]
[RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
public sealed class IndexModel : PageModel
{
    public const long MaxUploadBytes = 25 * 1024 * 1024;

    private readonly LicenseServerDbContext _db;

    public IndexModel(LicenseServerDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<ErrorReportImport> Imports { get; private set; } = Array.Empty<ErrorReportImport>();

    /// <summary>Known client names offered as suggestions (license requests + earlier imports). Free text is still allowed.</summary>
    public IReadOnlyList<string> KnownClients { get; private set; } = Array.Empty<string>();

    [BindProperty]
    [Required(ErrorMessage = "Enter a name for this import.")]
    [StringLength(200)]
    public string? ImportName { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Enter which client this report is from.")]
    [StringLength(200)]
    public string? ClientName { get; set; }

    [BindProperty]
    public IFormFile? ReportFile { get; set; }

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ReportFile is null || ReportFile.Length == 0)
        {
            ModelState.AddModelError(nameof(ReportFile), "Choose the exported .json or .csv file.");
        }
        else if (ReportFile.Length > MaxUploadBytes)
        {
            ModelState.AddModelError(nameof(ReportFile), "That file is larger than 25 MB.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        ParsedErrorReport parsed;
        try
        {
            await using var stream = ReportFile!.OpenReadStream();
            parsed = ErrorReportParser.Parse(ReportFile.FileName, stream);
        }
        catch (ErrorReportFormatException ex)
        {
            ErrorMessage = ex.Message;
            await LoadAsync();
            return Page();
        }

        var import = new ErrorReportImport
        {
            Id = Guid.NewGuid(),
            Name = ImportName!.Trim(),
            ClientName = ClientName!.Trim(),
            ImportedAtUtc = DateTime.UtcNow,
            ImportedBy = User.Identity?.Name ?? "admin",
            SourceFileName = Path.GetFileName(ReportFile.FileName),
            Format = parsed.Format,
            ReportFromUtc = parsed.FromUtc,
            ReportToUtc = parsed.ToUtc,
            ReportGeneratedAtUtc = parsed.GeneratedAtUtc,
            ApplicationVersion = parsed.ApplicationVersion,
            ReportTruncated = parsed.Truncated,
            ErrorCount = parsed.Entries.Count,
            Entries = parsed.Entries.ToList(),
            Correlations = (parsed.Correlations ?? Array.Empty<ErrorReportCorrelation>()).ToList(),
        };

        _db.ErrorReportImports.Add(import);
        await _db.SaveChangesAsync(cancellationToken);

        return RedirectToPage("Details", new { id = import.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var import = await _db.ErrorReportImports.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (import is not null)
        {
            _db.ErrorReportImports.Remove(import);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Imports = await _db.ErrorReportImports
            .AsNoTracking()
            .OrderByDescending(x => x.ImportedAtUtc)
            .ToListAsync();

        var fromRequests = await _db.LicenseRequests.AsNoTracking().Select(r => r.ClientName).ToListAsync();
        KnownClients = fromRequests
            .Concat(Imports.Select(i => i.ClientName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
