using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Services.Workflows.Numbering;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Workflows.Numbering;

/// <summary>
/// Default <see cref="IWorkflowNumberGenerator"/> — reads its format/reset policy from the
/// <c>WorkflowNumbering:*</c> system settings and draws the incremental segment from a durable per-period
/// counter row (<see cref="WorkflowNumberSequence"/>).
/// </summary>
public sealed class WorkflowNumberGenerator : IWorkflowNumberGenerator
{
    /// <summary>System-setting keys backing the "Workflow Numbering" group on Settings → System Settings → General.</summary>
    public const string EnabledKey = "WorkflowNumbering:Enabled";
    public const string PrefixKey = "WorkflowNumbering:Prefix";
    public const string DateFormatKey = "WorkflowNumbering:DateFormat";
    public const string ResetPolicyKey = "WorkflowNumbering:ResetPolicy";
    public const string AnchorDateKey = "WorkflowNumbering:AnchorDate";
    public const string PadWidthKey = "WorkflowNumbering:PadWidth";

    public const string DefaultPrefix = "WLW";
    public const string DefaultDateFormat = "ddMMyy";
    public const int DefaultPadWidth = 4;

    // Two instances creating the very first workflow of a new period both insert that period's counter row and
    // one loses on the unique index; an existing row hit concurrently raises a concurrency conflict instead.
    // Both are ordinary contention, not failures — re-read and retry rather than failing the workflow create.
    private const int MaxAllocationAttempts = 5;

    private readonly FHIRBridgeDbContext _dbContext;
    private readonly ISystemSettingsCache _settings;

    public WorkflowNumberGenerator(FHIRBridgeDbContext dbContext, ISystemSettingsCache settings)
    {
        _dbContext = dbContext;
        _settings = settings;
    }

    public async Task<string?> NextAsync(CancellationToken cancellationToken)
    {
        if (!await _settings.GetBoolAsync(EnabledKey, true, cancellationToken))
        {
            return null;
        }

        var format = await ReadFormatAsync(cancellationToken);
        var periodKey = WorkflowNumberPeriod.KeyFor(format.Policy, DateTime.UtcNow, format.AnchorDate);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var next = await AdvanceAsync(periodKey, cancellationToken);
                return Render(format, next);
            }
            catch (Exception ex) when (attempt < MaxAllocationAttempts && IsContention(ex))
            {
                // Drop the losing entry so the retry re-reads the row the winner just wrote, rather than
                // re-proposing the same stale value from the change tracker.
                foreach (var entry in _dbContext.ChangeTracker.Entries<WorkflowNumberSequence>().ToArray())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    public async Task<string> PreviewAsync(CancellationToken cancellationToken)
    {
        var format = await ReadFormatAsync(cancellationToken);
        var periodKey = WorkflowNumberPeriod.KeyFor(format.Policy, DateTime.UtcNow, format.AnchorDate);

        var current = await _dbContext.WorkflowNumberSequences
            .AsNoTracking()
            .Where(sequence => sequence.PeriodKey == periodKey)
            .Select(sequence => (long?)sequence.LastValue)
            .FirstOrDefaultAsync(cancellationToken);

        return Render(format, (current ?? 0) + 1);
    }

    /// <summary>
    /// Consumes the next value for <paramref name="periodKey"/>. Deliberately does NOT open its own
    /// transaction — the caller (SqlWorkflowDefinitionStore.SaveAsync) already runs inside one, and the
    /// allocation must commit or roll back together with the workflow it numbers.
    /// </summary>
    private async Task<long> AdvanceAsync(string periodKey, CancellationToken cancellationToken)
    {
        var sequence = await _dbContext.WorkflowNumberSequences
            .FirstOrDefaultAsync(candidate => candidate.PeriodKey == periodKey, cancellationToken);

        if (sequence is null)
        {
            // First workflow of this period — the period key having no row IS the reset. Nothing resets
            // counters on a schedule; a new period simply starts here at 1.
            sequence = new WorkflowNumberSequence(periodKey, 0);
            await _dbContext.WorkflowNumberSequences.AddAsync(sequence, cancellationToken);
        }

        var next = sequence.Advance();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return next;
    }

    private async Task<NumberFormat> ReadFormatAsync(CancellationToken cancellationToken)
    {
        var prefix = await _settings.GetStringAsync(PrefixKey, DefaultPrefix, cancellationToken);
        var dateFormat = await _settings.GetStringAsync(DateFormatKey, DefaultDateFormat, cancellationToken);
        var policyText = await _settings.GetStringAsync(ResetPolicyKey, nameof(WorkflowNumberResetPolicy.Daily), cancellationToken);
        var anchorDate = await _settings.GetStringAsync(AnchorDateKey, WorkflowNumberPeriod.DefaultAnchorDate, cancellationToken);
        var padWidth = await _settings.GetIntAsync(PadWidthKey, DefaultPadWidth, cancellationToken);

        // An unrecognized policy falls back to Daily rather than throwing: a bad settings row must not make
        // workflow creation fail outright.
        if (!Enum.TryParse<WorkflowNumberResetPolicy>(policyText?.Trim(), ignoreCase: true, out var policy))
        {
            policy = WorkflowNumberResetPolicy.Daily;
        }

        return new NumberFormat(
            string.IsNullOrWhiteSpace(prefix) ? DefaultPrefix : prefix.Trim(),
            string.IsNullOrWhiteSpace(dateFormat) ? DefaultDateFormat : dateFormat.Trim(),
            policy,
            anchorDate,
            Math.Clamp(padWidth, 1, 12));
    }

    private static string Render(NumberFormat format, long value)
    {
        string datePart;
        try
        {
            datePart = DateTime.UtcNow.ToString(format.DateFormat, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            // Same stance as an unrecognized reset policy: a malformed custom format degrades to the default
            // rather than blocking the create.
            datePart = DateTime.UtcNow.ToString(DefaultDateFormat, System.Globalization.CultureInfo.InvariantCulture);
        }

        return $"{format.Prefix}-{datePart}-{value.ToString().PadLeft(format.PadWidth, '0')}";
    }

    /// <summary>True for the two ways concurrent allocation surfaces: a lost optimistic-concurrency race on an
    /// existing counter row, or a unique-index violation from two instances inserting the same period's first row.</summary>
    private static bool IsContention(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || (exception is DbUpdateException && exception.InnerException is not null);

    private sealed record NumberFormat(
        string Prefix,
        string DateFormat,
        WorkflowNumberResetPolicy Policy,
        string? AnchorDate,
        int PadWidth);
}
