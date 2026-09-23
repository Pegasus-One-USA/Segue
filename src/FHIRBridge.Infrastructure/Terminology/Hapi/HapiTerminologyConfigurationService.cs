using System.Globalization;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Terminology;
using FHIRBridge.Domain.Entities.Terminology;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <inheritdoc cref="IHapiTerminologyConfigurationService"/>
public sealed class HapiTerminologyConfigurationService : IHapiTerminologyConfigurationService
{
    private readonly HapiTerminologySystemRegistry _registry;
    private readonly ISystemSettingsCache _settings;
    private readonly ISystemSettingsService _settingsService;
    private readonly ISecretWriter _secretWriter;
    private readonly IAppSecretMetadataProvider _metadataProvider;
    private readonly FHIRBridgeDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HapiTerminologyConfigurationService> _logger;
    private readonly ITerminologyStatusNotifier? _statusNotifier;

    public HapiTerminologyConfigurationService(
        HapiTerminologySystemRegistry registry,
        ISystemSettingsCache settings,
        ISystemSettingsService settingsService,
        ISecretWriter secretWriter,
        IAppSecretMetadataProvider metadataProvider,
        FHIRBridgeDbContext db,
        IServiceProvider serviceProvider,
        ILogger<HapiTerminologyConfigurationService> logger,
        // Nullable by design — only the API host registers one (it owns the hub). The Worker, where the
        // scheduled syncs run, resolves null here and skips the push; see ITerminologyStatusNotifier.
        ITerminologyStatusNotifier? statusNotifier = null)
    {
        _registry = registry;
        _settings = settings;
        _settingsService = settingsService;
        _secretWriter = secretWriter;
        _metadataProvider = metadataProvider;
        _db = db;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _statusNotifier = statusNotifier;
    }

    /// <summary>
    /// Builds all 13 rows, strictly one at a time.
    ///
    /// Do NOT parallelise this. It is tempting, because the slow part on a machine that cannot reach Key
    /// Vault is <see cref="BuildCredentialFieldsAsync"/> paying its own connect-and-retry timeout thirteen
    /// times over. But those lookups are not DbContext-free: the secret provider is a composite whose
    /// <c>DbSecretStore</c> leg reads app-provisioned secrets through this same scoped
    /// <see cref="FHIRBridgeDbContext"/>, so overlapping them throws "A second operation was started on this
    /// context instance" and the whole endpoint 500s. Tried, reverted; the settings reads in
    /// <see cref="BuildDtoAsync"/> share the context too.
    ///
    /// Making this genuinely concurrent needs each lookup to own its DbContext — a scope per descriptor via
    /// <c>IServiceScopeFactory</c> — rather than a <c>Task.WhenAll</c> over the shared one.
    /// </summary>
    public async Task<IReadOnlyList<HapiTerminologyConfigurationDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<HapiTerminologyConfigurationDto>(_registry.All.Count);
        foreach (var descriptor in _registry.All)
        {
            result.Add(await BuildDtoAsync(descriptor, cancellationToken));
        }

        return result;
    }

    public async Task<HapiTerminologyConfigurationDto> GetAsync(string code, CancellationToken cancellationToken) =>
        await BuildDtoAsync(_registry.Get(code), cancellationToken);

    public async Task<HapiTerminologyConfigurationDto> UpdateAsync(
        string code, UpdateHapiTerminologyConfigurationRequest request, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(code);

        if (!string.Equals(request.Frequency, "Weekly", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Frequency, "Monthly", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Frequency must be Weekly or Monthly.");
        }

        if (!TimeOnly.TryParseExact(request.ExecutionTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new InvalidOperationException("Execution time must use HH:mm.");
        }

        if (descriptor.DownloadApiUrlSettingKey is not null && !string.IsNullOrWhiteSpace(request.DownloadApiUrl))
        {
            if (!Uri.TryCreate(request.DownloadApiUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"The {descriptor.DisplayName} download URL must be absolute.");
            }

            await _settingsService.SetAsync(descriptor.DownloadApiUrlSettingKey, request.DownloadApiUrl.Trim(),
                $"Official {descriptor.DisplayName} release-download endpoint (shared by the HAPI sync and the legacy database sync).", cancellationToken);
        }

        await WriteCredentialsAsync(descriptor, request.CredentialValues, cancellationToken);

        var prefix = descriptor.SettingsKeyPrefix;
        await _settingsService.SetAsync($"{prefix}:SchedulerEnabled", request.SchedulerEnabled.ToString(),
            $"Master switch for automatically downloading {descriptor.DisplayName} and loading it into the terminology server, on a schedule.", cancellationToken);
        await _settingsService.SetAsync($"{prefix}:Frequency",
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.Frequency.ToLowerInvariant()),
            $"{descriptor.DisplayName} terminology-server sync frequency: Weekly or Monthly.", cancellationToken);
        await _settingsService.SetAsync($"{prefix}:ExecutionTime", request.ExecutionTime,
            $"Local execution time for the scheduled {descriptor.DisplayName} terminology-server sync (HH:mm).", cancellationToken);

        return await BuildDtoAsync(descriptor, cancellationToken);
    }

    public async Task RunAndRecordHistoryAsync(string code, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(code);
        var history = new HapiTerminologyImportHistory(descriptor.Code);
        _db.HapiTerminologyImportHistory.Add(history);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("RunAndRecordHistoryAsync starting for {Code} (history {HistoryId}).", code, history.Id);
        await NotifyStatusAsync(new TerminologyStatusChangedEvent(descriptor.Code, "Running", DateTimeOffset.UtcNow));

        try
        {
            var outcome = await descriptor.RunAsync(_serviceProvider, cancellationToken);
            _logger.LogInformation("RunAndRecordHistoryAsync: {Code} sync returned {Count} concepts; marking history {HistoryId} complete.", code, outcome.Count, history.Id);
            history.Complete(outcome.Count, outcome.Version);
            await _settingsService.SetAsync($"{descriptor.SettingsKeyPrefix}:LastRunUtc", DateTime.UtcNow.ToString("O"), null, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "RunAndRecordHistoryAsync: {Code} sync failed; marking history {HistoryId} failed.", code, history.Id);
            history.Fail(exception.Message);

            // A sync failure often comes from a SaveChangesAsync call partway through a large concept
            // batch (e.g. a truncation error) — those entities stay tracked as "Added" even though the
            // save never committed. Left in place, the finally block's own SaveChangesAsync below would
            // try to re-save that same broken batch, hit the identical error again, throw a SECOND time
            // uncaught, and this history.Fail(...) write would never actually reach the database —
            // exactly what caused every "stuck at Running forever" row this session. Detach everything
            // except the history row itself so the finally block can cleanly persist just the failure.
            foreach (var entry in _db.ChangeTracker.Entries().Where(e => !ReferenceEquals(e.Entity, history)).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
        finally
        {
            await _db.SaveChangesAsync(CancellationToken.None);
            _logger.LogInformation("RunAndRecordHistoryAsync finished for {Code} (history {HistoryId}), final status {Status}.", code, history.Id, history.Status);

            // In the finally block, not at the end of each branch: this is the client's ONLY signal that the
            // sync ended now that the history poll is gone, so it has to fire on the failure path too — and
            // the catch above deliberately swallows the exception, so both paths converge here. A row whose
            // terminal event never arrives is stuck showing "Running" until the page is reloaded.
            await NotifyStatusAsync(new TerminologyStatusChangedEvent(
                descriptor.Code,
                history.Status,
                DateTimeOffset.UtcNow,
                history.ImportedConceptCount,
                history.Version,
                history.ErrorMessage));
        }
    }

    /// <summary>Fire-and-forget by intent: a push that fails (no hub registered, client gone, transport
    /// down) must never fail the import that just succeeded, so this swallows and logs rather than
    /// propagating. Called from a finally block, where throwing would also mask the original exception.</summary>
    private async Task NotifyStatusAsync(TerminologyStatusChangedEvent statusEvent)
    {
        if (_statusNotifier is null)
        {
            return;
        }

        try
        {
            await _statusNotifier.NotifyAsync(statusEvent, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Failed to push {Status} status for {Code}; the import itself was unaffected.",
                statusEvent.Status, statusEvent.Code);
        }
    }

    public async Task<IReadOnlyList<HapiTerminologyImportHistoryEntryDto>> GetHistoryAsync(string code, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(code);
        return await _db.HapiTerminologyImportHistory
            .Where(x => x.CodeSystem == descriptor.Code)
            .OrderByDescending(x => x.StartedOnUtc)
            .Take(20)
            .Select(x => new HapiTerminologyImportHistoryEntryDto(
                x.Id, x.Version, x.StartedOnUtc, x.CompletedOnUtc, x.ImportedConceptCount, x.Status, x.ErrorMessage))
            .ToListAsync(cancellationToken);
    }

    public async Task<HapiTerminologyVersionCheckResultDto> ScanForNewVersionAsync(string code, CancellationToken cancellationToken)
    {
        var descriptor = _registry.Get(code);
        var storedVersion = await GetStoredVersionAsync(descriptor, cancellationToken);

        // A system whose source publishes no discoverable "latest version" pointer still needs an update
        // when it has never actually been downloaded. Reporting updateAvailable:false unconditionally here
        // made the portal claim "All code systems are up to date" while 7 of the 13 had never run at all —
        // "we cannot check for a NEWER version" is not the same claim as "what we hold is current".
        if (descriptor.CheckLatestVersionAsync is null)
        {
            var hasConcepts = await HasStoredConceptsAsync(descriptor, cancellationToken);
            return new HapiTerminologyVersionCheckResultDto(
                descriptor.Code, false, storedVersion, null, !hasConcepts, null);
        }

        try
        {
            var latestVersion = await descriptor.CheckLatestVersionAsync(_serviceProvider, cancellationToken);

            // Compare like with like. storedVersion came out of a varchar(32) column and has therefore
            // already been clipped, while latestVersion is whatever the publisher serves — CMS's is a
            // 41-character release filename. Comparing the raw upstream value against the clipped stored one
            // never matches, so updateAvailable stays true forever and the portal's auto-sync re-downloads
            // and re-imports the entire code system on every page load.
            var comparableLatest = HapiTerminologyImportHistory.ClipVersion(latestVersion);
            var updateAvailable = latestVersion is not null
                && !string.Equals(comparableLatest, storedVersion, StringComparison.OrdinalIgnoreCase);

            // "Up to date" is a claim about the CONCEPTS, but storedVersion is read from the import-history
            // table — two separate sources of truth that can disagree. A succeeded history row whose concepts
            // were later cleared (or whose import wrote history but no data) would otherwise report no update
            // available and never auto-sync, leaving the system permanently empty and silently claiming to be
            // current. Treat "nothing actually stored" as needing a sync regardless of what history says.
            // Note the deliberate absence of a "storedVersion is not null" guard: a system that has NEVER
            // been downloaded has no history row at all, so requiring one skipped precisely the systems
            // most in need of a sync and reported them as up to date.
            if (!updateAvailable && !await HasStoredConceptsAsync(descriptor, cancellationToken))
            {
                updateAvailable = true;
            }

            return new HapiTerminologyVersionCheckResultDto(descriptor.Code, true, storedVersion, latestVersion, updateAvailable, null);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "ScanForNewVersionAsync: {Code} version check failed.", code);

            // A failed check (bad/absent credentials, source unreachable) tells us nothing about whether
            // what we hold is current. If nothing is stored at all we still know an update is needed, so
            // don't let the failure mask that — otherwise a credential-less system reports "Scan failed"
            // and is simultaneously counted as up to date.
            var hasConceptsAfterFailure = await HasStoredConceptsAsync(descriptor, cancellationToken);
            return new HapiTerminologyVersionCheckResultDto(
                descriptor.Code, true, storedVersion, null, !hasConceptsAfterFailure, exception.Message);
        }
    }

    public async Task<IReadOnlyList<HapiTerminologyVersionCheckResultDto>> ScanAllForNewVersionsAsync(CancellationToken cancellationToken)
    {
        // Sequential, not Task.WhenAll: every scan shares this instance's single DbContext (for
        // GetStoredVersionAsync), and EF Core throws if two operations run concurrently on it.
        var results = new List<HapiTerminologyVersionCheckResultDto>(_registry.All.Count);
        foreach (var descriptor in _registry.All)
        {
            results.Add(await ScanForNewVersionAsync(descriptor.Code, cancellationToken));
        }

        return results;
    }

    /// <summary>True when this system actually has concepts loaded. Deliberately a COUNT-free existence
    /// check: TRM_CONCEPT holds hundreds of thousands of rows and all we need to know is "any at all".</summary>
    private async Task<bool> HasStoredConceptsAsync(HapiTerminologySystemDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(descriptor.CodeSystemUri))
        {
            // No URI recorded for this system, so concept presence cannot be established. Say "yes" rather
            // than "no": a false negative here would auto-start a full re-import on every page load.
            return true;
        }

        // Joined by hand rather than through a navigation property, because TrmConcept has none — and
        // because HAPI's schema has a trap here: TrmConcept.CodeSystemPid points at the VERSION row
        // (TrmCodeSystemVer), not at TrmCodeSystem. Matching it straight against TrmCodeSystem.Pid would
        // compare two unrelated identifier spaces and quietly return the wrong answer.
        var versionPids = _db.TrmCodeSystemVers
            .Where(v => _db.TrmCodeSystems
                .Where(cs => cs.CodeSystemUri == descriptor.CodeSystemUri)
                .Select(cs => cs.Pid)
                .Contains(v.CodeSystemPid))
            .Select(v => v.Pid);

        return await _db.TrmConcepts.AnyAsync(c => versionPids.Contains(c.CodeSystemPid), cancellationToken);
    }

    private async Task<string?> GetStoredVersionAsync(HapiTerminologySystemDescriptor descriptor, CancellationToken cancellationToken) =>
        await _db.HapiTerminologyImportHistory
            .Where(x => x.CodeSystem == descriptor.Code && x.Status == "Succeeded")
            .OrderByDescending(x => x.StartedOnUtc)
            .Select(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<HapiTerminologyConfigurationDto> BuildDtoAsync(HapiTerminologySystemDescriptor descriptor, CancellationToken cancellationToken)
    {
        var prefix = descriptor.SettingsKeyPrefix;
        // Defaults to enabled: a terminology system that is never synced is of no use, and every one of
        // the 13 is expected to be kept current. An operator who genuinely wants one off still turns it
        // off explicitly, and that stored "false" is honoured here — only the ABSENCE of a setting means
        // enabled, so this does not silently re-enable anything anybody has deliberately disabled.
        var schedulerEnabled = await _settings.GetBoolAsync($"{prefix}:SchedulerEnabled", true, cancellationToken);
        var frequency = await _settings.GetStringAsync($"{prefix}:Frequency", "Monthly", cancellationToken);
        var executionTime = await _settings.GetStringAsync($"{prefix}:ExecutionTime", "00:00", cancellationToken);
        var lastRunRaw = await _settings.GetStringAsync($"{prefix}:LastRunUtc", string.Empty, cancellationToken);
        var lastRunUtc = DateTime.TryParse(lastRunRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : (DateTime?)null;

        var credentials = await BuildCredentialFieldsAsync(descriptor, cancellationToken);
        var downloadApiUrl = descriptor.DownloadApiUrlSettingKey is null
            ? null
            : await _settings.GetStringAsync(descriptor.DownloadApiUrlSettingKey, string.Empty, cancellationToken);

        return new HapiTerminologyConfigurationDto(
            descriptor.Code, descriptor.DisplayName, schedulerEnabled, frequency,
            HapiTerminologySystemDescriptor.FrequencyOptions, executionTime, lastRunUtc, credentials, downloadApiUrl);
    }

    private async Task<IReadOnlyList<HapiCredentialFieldDto>> BuildCredentialFieldsAsync(
        HapiTerminologySystemDescriptor descriptor, CancellationToken cancellationToken)
    {
        switch (descriptor.CredentialKind)
        {
            case HapiCredentialKind.LoincBasicAuth:
                var username = await _metadataProvider.GetMetadataAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.UsernameSecretName), cancellationToken);
                var password = await _metadataProvider.GetMetadataAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.PasswordSecretName), cancellationToken);
                return new[]
                {
                    new HapiCredentialFieldDto("username", "Username", username.Provisioned),
                    new HapiCredentialFieldDto("password", "Password", password.Provisioned),
                };
            case HapiCredentialKind.UtsApiKey:
                var apiKey = await _metadataProvider.GetMetadataAsync(new SecretReference(UtsCredentialNames.VaultName, UtsCredentialNames.ApiKeySecretName), cancellationToken);
                return new[] { new HapiCredentialFieldDto("apiKey", "UTS API Key", apiKey.Provisioned) };
            default:
                return Array.Empty<HapiCredentialFieldDto>();
        }
    }

    private async Task WriteCredentialsAsync(
        HapiTerminologySystemDescriptor descriptor, IReadOnlyDictionary<string, string>? values, CancellationToken cancellationToken)
    {
        if (values is null || descriptor.CredentialKind == HapiCredentialKind.None)
        {
            return;
        }

        switch (descriptor.CredentialKind)
        {
            case HapiCredentialKind.LoincBasicAuth:
                if (values.TryGetValue("username", out var username) && !string.IsNullOrWhiteSpace(username))
                {
                    await _secretWriter.WriteSecretAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.UsernameSecretName), username, cancellationToken);
                }
                if (values.TryGetValue("password", out var password) && !string.IsNullOrWhiteSpace(password))
                {
                    await _secretWriter.WriteSecretAsync(new SecretReference(LoincConfigurationService.VaultName, LoincConfigurationService.PasswordSecretName), password, cancellationToken);
                }
                break;
            case HapiCredentialKind.UtsApiKey:
                if (values.TryGetValue("apiKey", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
                {
                    await _secretWriter.WriteSecretAsync(new SecretReference(UtsCredentialNames.VaultName, UtsCredentialNames.ApiKeySecretName), apiKey, cancellationToken);
                }
                break;
        }
    }
}
