using System.Data.Common;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Licensing;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// Singleton <see cref="ILicenseService"/> implementation. Resolves the scoped
/// <see cref="ISystemSettingRepository"/> through a fresh <see cref="IServiceScopeFactory"/>-created scope
/// per lookup — the same per-lookup-scope pattern <c>InProcessSystemSettingsCache</c> and
/// <c>CachedCurrentTenantResolver</c> use to let a singleton reach scoped, DB-backed state safely.
///
/// <see cref="Current"/> starts as <see cref="LicenseStatus.Unlicensed"/> and stays that way until
/// something calls <see cref="ReloadAsync"/> — the constructor deliberately does no I/O. Program.cs (both
/// the Api and Worker hosts) calls <see cref="ReloadAsync"/> once at startup, right after
/// <c>AppSecretProvisioner.ProvisionAsync</c>.
///
/// This stage is verification/reporting only: nothing here blocks or gates any product behavior on the
/// resolved <see cref="LicenseStatus"/>.
/// </summary>
public sealed class LicenseService : ILicenseService
{
    /// <summary>SystemSetting key the currently-applied license token is persisted under.</summary>
    private const string LicenseTokenSettingKey = "License:Token";

    /// <summary>Env var fallback, checked when no SystemSetting row is present.</summary>
    private const string LicenseTokenEnvVar = "FHIRBRIDGE_LICENSE";

    /// <summary>Env var naming a file whose contents are the token — checked last.</summary>
    private const string LicenseTokenFileEnvVar = "FHIRBRIDGE_LICENSE_FILE";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _lock = new();

    public LicenseService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public LicenseStatus Current { get; private set; } = LicenseStatus.Unlicensed;

    public string? CurrentRawToken { get; private set; }

    public bool HasFeature(string featureKey) =>
        Current.Features.Contains(featureKey, StringComparer.OrdinalIgnoreCase);

    public async Task<LicenseApplyResult> ApplyAsync(string licenseToken, CancellationToken cancellationToken)
    {
        // enforceActivationWindow: true — this IS the "apply a freshly generated key" path (as opposed to
        // ReloadAsync re-resolving an already-applied token on every process restart, which must never
        // re-check this same deadline). See SignedLicenseValidator.Validate's remarks for why.
        var status = SignedLicenseValidator.Validate(licenseToken, enforceActivationWindow: true);
        if (status.State == LicenseState.Invalid)
        {
            // Rejected at the door: nothing is written and Current is left exactly as it was, so a bad
            // token pasted into the admin screen can never brick the license the next time the process
            // restarts (which would otherwise re-resolve this same bad token from SystemSetting on boot).
            return new LicenseApplyResult(
                false, status.InvalidReason ?? "The license token failed verification.", null);
        }

        // Re-applying the exact same token as the one already current — a back-to-back double-submit,
        // or a renewal screen the admin resubmits without changing anything — is a no-op: nothing is
        // re-persisted and no new LicenseHistoryEntry row is added, so the history table doesn't
        // accumulate duplicate rows for a license that never actually changed. Deliberately NOT
        // restricted to State == Active: CurrentRawToken is non-null for Grace and Expired too (see
        // ReloadAsync), and re-applying the same token while it's sitting in Grace/Expired is at least
        // as likely as while Active, so it gets the same no-op treatment.
        lock (_lock)
        {
            if (CurrentRawToken is not null && CurrentRawToken == licenseToken)
            {
                return new LicenseApplyResult(true, null, Current, true);
            }
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            // A license minted against a specific LicenseRequest (requestKey claim present) must match
            // THIS install's own stored request key, proving it was minted for this install's request and
            // not one copied from a different customer. A token with no requestKey claim at all skips this
            // entirely — every license minted before this feature existed, or one deliberately minted
            // without a request tied to it, keeps working unchanged.
            if (!string.IsNullOrEmpty(status.RequestKey))
            {
                var requestRepository = scope.ServiceProvider.GetRequiredService<ILicenseRequestRepository>();
                var ourRequest = await requestRepository.GetAsync(cancellationToken);
                if (ourRequest is null || !string.Equals(ourRequest.UniqueKey, status.RequestKey, StringComparison.Ordinal))
                {
                    return new LicenseApplyResult(
                        false,
                        "This license was minted for a different license request than this install's own — "
                        + "it can't be applied here.",
                        null);
                }
            }

            var repository = scope.ServiceProvider.GetRequiredService<ISystemSettingRepository>();
            await repository.UpsertAsync(
                LicenseTokenSettingKey, licenseToken, "Signed product license token.", cancellationToken);

            // Audit trail of every license ever successfully applied — Current/CurrentRawToken (and the
            // SystemSetting row above) always reflect only the most recent one; this table keeps the rest.
            var historyRepository = scope.ServiceProvider.GetRequiredService<ILicenseHistoryRepository>();
            await historyRepository.AddAsync(
                new LicenseHistoryEntry(
                    Guid.NewGuid(), licenseToken, DateTime.UtcNow, status.CustomerName, status.Edition,
                    status.State.ToString(), status.ExpiresUtc),
                cancellationToken);
        }

        lock (_lock)
        {
            Current = status;
            CurrentRawToken = licenseToken;
        }

        return new LicenseApplyResult(true, null, status);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var token = await ResolveTokenAsync(cancellationToken);
        var status = SignedLicenseValidator.Validate(token);

        lock (_lock)
        {
            Current = status;
            CurrentRawToken = status.State == LicenseState.Invalid ? null : token;
        }
    }

    /// <summary>Checks, in order: (a) the SystemSetting row, (b) <see cref="LicenseTokenEnvVar"/>, (c) the
    /// file named by <see cref="LicenseTokenFileEnvVar"/>. First one found wins.</summary>
    private async Task<string?> ResolveTokenAsync(CancellationToken cancellationToken)
    {
        SystemSetting? setting = null;
        using (var scope = _scopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISystemSettingRepository>();
            try
            {
                setting = await repository.GetByKeyAsync(LicenseTokenSettingKey, cancellationToken);
            }
            catch (DbException)
            {
                // The SystemSettings table doesn't exist yet (e.g. this reload runs during startup before
                // migrations apply) — fall through to the env var / file sources below instead of failing
                // the whole reload. Mirrors InProcessSystemSettingsCache's handling of the same race.
            }
        }

        if (!string.IsNullOrWhiteSpace(setting?.Value))
        {
            return setting.Value;
        }

        var envToken = Environment.GetEnvironmentVariable(LicenseTokenEnvVar);
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return envToken;
        }

        var licenseFilePath = Environment.GetEnvironmentVariable(LicenseTokenFileEnvVar);
        if (!string.IsNullOrWhiteSpace(licenseFilePath) && File.Exists(licenseFilePath))
        {
            var fileContents = await File.ReadAllTextAsync(licenseFilePath, cancellationToken);
            return string.IsNullOrWhiteSpace(fileContents) ? null : fileContents.Trim();
        }

        return null;
    }
}
