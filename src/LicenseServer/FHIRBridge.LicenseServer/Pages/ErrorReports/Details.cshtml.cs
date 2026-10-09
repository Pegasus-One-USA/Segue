using FHIRBridge.LicenseServer.Data;
using FHIRBridge.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.LicenseServer.Pages.ErrorReports;

public sealed class DetailsModel : PageModel
{
    public const int PageSize = 50;

    private readonly LicenseServerDbContext _db;

    public DetailsModel(LicenseServerDbContext db)
    {
        _db = db;
    }

    public sealed record ErrorRowView(ErrorReportEntry Entry, ErrorReportCorrelation? Correlation);

    public sealed record Count(string Key, int Value);

    public sealed record Day(DateTime DayUtc, int Value);

    public sealed record Signature(
        long SampleEntryId, string ExceptionType, string? Module, string SampleMessage, int Total, int Open, DateTime LastSeenUtc, string? SampleReference);

    public ErrorReportImport Import { get; private set; } = null!;

    public int Total { get; private set; }

    public int Open { get; private set; }

    public int Resolved { get; private set; }

    public int Critical { get; private set; }

    public int SelfFix { get; private set; }

    public int NeedSupport { get; private set; }

    public IReadOnlyList<Count> BySeverity { get; private set; } = Array.Empty<Count>();

    public IReadOnlyList<Count> ByCategory { get; private set; } = Array.Empty<Count>();

    public IReadOnlyList<Count> ByModule { get; private set; } = Array.Empty<Count>();

    public IReadOnlyList<Day> ByDay { get; private set; } = Array.Empty<Day>();

    public IReadOnlyList<Signature> TopSignatures { get; private set; } = Array.Empty<Signature>();

    public IReadOnlyList<ErrorReportEntry> Entries { get; private set; } = Array.Empty<ErrorReportEntry>();

    /// <summary>Full rows behind the "Most frequent errors" examples, so a click can open the same detail popup.</summary>
    public IReadOnlyDictionary<long, ErrorReportEntry> SignatureEntries { get; private set; } = new Dictionary<long, ErrorReportEntry>();

    private IReadOnlyDictionary<string, ErrorReportCorrelation> _correlations = new Dictionary<string, ErrorReportCorrelation>();

    public ErrorRowView Row(ErrorReportEntry entry) =>
        new(entry, entry.CorrelationId is not null && _correlations.TryGetValue(entry.CorrelationId, out var c) ? c : null);

    public int FilteredCount { get; private set; }

    public int PageNumber { get; private set; } = 1;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(FilteredCount / (double)PageSize));

    [BindProperty(SupportsGet = true)]
    public string? Severity { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Module { get; set; }

    /// <summary>Start of the "when" filter, entered as UTC (datetime-local input).</summary>
    [BindProperty(SupportsGet = true)]
    public DateTime? FromUtc { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? ToUtc { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? CorrelationId { get; set; }

    /// <summary>True if this import carries any correlation timelines at all.</summary>
    public bool ImportHasCorrelations { get; private set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageIndex { get; set; } = 1;

    public int MaxDay => ByDay.Count == 0 ? 1 : Math.Max(1, ByDay.Max(d => d.Value));

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var import = await _db.ErrorReportImports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (import is null) return NotFound();
        Import = import;

        // Aggregates exclude the heavy text columns; the table below loads them for one page at a time.
        var rows = await _db.ErrorReportEntries.AsNoTracking()
            .Where(x => x.ImportId == id)
            .Select(x => new
            {
                x.OccurredOnUtc, x.Severity, x.Category, x.Module, x.ExceptionType, x.Status, x.WhatToDo,
                x.Id, x.ErrorReferenceId,
                Message = x.Message.Length > 200 ? x.Message.Substring(0, 200) : x.Message,
            })
            .ToListAsync(cancellationToken);

        bool IsResolved(string status) => string.Equals(status, "Resolved", StringComparison.OrdinalIgnoreCase);

        Total = rows.Count;
        Resolved = rows.Count(r => IsResolved(r.Status));
        Open = Total - Resolved;
        Critical = rows.Count(r => string.Equals(r.Severity, "Critical", StringComparison.OrdinalIgnoreCase));
        SelfFix = rows.Count(r => r.WhatToDo == "Check your configuration");
        NeedSupport = Total - SelfFix;

        static IReadOnlyList<Count> Group(IEnumerable<string?> keys, string fallback) =>
            keys.GroupBy(k => string.IsNullOrWhiteSpace(k) ? fallback : k!)
                .Select(g => new Count(g.Key, g.Count()))
                .OrderByDescending(c => c.Value).ThenBy(c => c.Key)
                .ToList();

        BySeverity = Group(rows.Select(r => r.Severity), "Unknown");
        ByCategory = Group(rows.Select(r => r.Category), "Uncategorized");
        ByModule = Group(rows.Select(r => r.Module), "Unknown");
        ByDay = rows.Where(r => r.OccurredOnUtc != default)
            .GroupBy(r => r.OccurredOnUtc.Date)
            .Select(g => new Day(g.Key, g.Count()))
            .OrderBy(d => d.DayUtc)
            .ToList();
        TopSignatures = rows
            .GroupBy(r => $"{r.ExceptionType}|{r.Module}|{(r.Message.Length > 80 ? r.Message[..80] : r.Message)}")
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.OccurredOnUtc).First();
                return new Signature(
                    latest.Id, latest.ExceptionType, latest.Module, latest.Message, g.Count(), g.Count(r => !IsResolved(r.Status)),
                    latest.OccurredOnUtc, latest.ErrorReferenceId);
            })
            .OrderByDescending(s => s.Total).ThenByDescending(s => s.LastSeenUtc)
            .Take(15)
            .ToList();

        var sampleIds = TopSignatures.Select(t => t.SampleEntryId).ToList();
        SignatureEntries = await _db.ErrorReportEntries.AsNoTracking()
            .Where(x => sampleIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var query = _db.ErrorReportEntries.AsNoTracking().Where(x => x.ImportId == id);
        if (!string.IsNullOrWhiteSpace(Severity)) query = query.Where(x => x.Severity == Severity);
        if (!string.IsNullOrWhiteSpace(Category)) query = query.Where(x => x.Category == Category);
        if (!string.IsNullOrWhiteSpace(Module)) query = query.Where(x => x.Module == Module);
        if (!string.IsNullOrWhiteSpace(CorrelationId))
        {
            var correlationTerm = CorrelationId.Trim();
            query = query.Where(x => x.CorrelationId != null && EF.Functions.ILike(x.CorrelationId, "%" + correlationTerm + "%"));
        }

        if (FromUtc.HasValue)
        {
            var from = DateTime.SpecifyKind(FromUtc.Value, DateTimeKind.Utc);
            query = query.Where(x => x.OccurredOnUtc >= from);
        }

        if (ToUtc.HasValue)
        {
            // datetime-local has minute precision: include the whole selected minute.
            var to = DateTime.SpecifyKind(ToUtc.Value, DateTimeKind.Utc).AddMinutes(1);
            query = query.Where(x => x.OccurredOnUtc < to);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(x =>
                EF.Functions.ILike(x.Message, "%" + term + "%")
                || EF.Functions.ILike(x.ExceptionType, "%" + term + "%")
                || (x.ErrorReferenceId != null && EF.Functions.ILike(x.ErrorReferenceId, "%" + term + "%"))
                || (x.CorrelationId != null && EF.Functions.ILike(x.CorrelationId, "%" + term + "%"))
                || (x.WorkflowId != null && EF.Functions.ILike(x.WorkflowId, "%" + term + "%"))
                || (x.EndpointId != null && EF.Functions.ILike(x.EndpointId, "%" + term + "%"))
                || (x.StackTrace != null && EF.Functions.ILike(x.StackTrace, "%" + term + "%")));
        }

        FilteredCount = await query.CountAsync(cancellationToken);
        PageNumber = Math.Clamp(PageIndex, 1, PageCount);
        Entries = await query
            .OrderByDescending(x => x.OccurredOnUtc)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        ImportHasCorrelations = await _db.ErrorReportCorrelations.AnyAsync(x => x.ImportId == id, cancellationToken);

        var correlationIds = Entries.Concat(SignatureEntries.Values)
            .Select(x => x.CorrelationId).Where(x => x != null).Distinct().ToList();
        _correlations = (await _db.ErrorReportCorrelations.AsNoTracking()
                .Where(x => x.ImportId == id && correlationIds.Contains(x.CorrelationId))
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.CorrelationId)
            .ToDictionary(g => g.Key, g => g.First());

        return Page();
    }

    /// <summary>Deletes this imported error group (the import and all of its error rows).</summary>
    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var import = await _db.ErrorReportImports.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (import is not null)
        {
            _db.ErrorReportImports.Remove(import);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Redirect("/ErrorReports");
    }

    public string PageUrl(int page) =>
        $"/ErrorReports/Details/{Import.Id}?p={page}"
        + (string.IsNullOrWhiteSpace(Severity) ? "" : "&Severity=" + Uri.EscapeDataString(Severity))
        + (string.IsNullOrWhiteSpace(Category) ? "" : "&Category=" + Uri.EscapeDataString(Category))
        + (string.IsNullOrWhiteSpace(Module) ? "" : "&Module=" + Uri.EscapeDataString(Module))
        + (FromUtc.HasValue ? "&FromUtc=" + Uri.EscapeDataString(FromUtc.Value.ToString("yyyy-MM-ddTHH:mm")) : "")
        + (ToUtc.HasValue ? "&ToUtc=" + Uri.EscapeDataString(ToUtc.Value.ToString("yyyy-MM-ddTHH:mm")) : "")
        + (string.IsNullOrWhiteSpace(Search) ? "" : "&Search=" + Uri.EscapeDataString(Search))
        + (string.IsNullOrWhiteSpace(CorrelationId) ? "" : "&CorrelationId=" + Uri.EscapeDataString(CorrelationId));

    public static int Percent(int value, IReadOnlyList<Count> list) =>
        list.Count == 0 ? 0 : (int)Math.Round(value * 100.0 / Math.Max(1, list.Max(c => c.Value)));
}
