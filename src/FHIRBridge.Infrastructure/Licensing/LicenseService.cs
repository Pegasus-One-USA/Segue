using System.Data.Common;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// Singleton <see cref="ILicenseService"/> implementation. Resolves the scoped
/// <see cref="ISystemSettingRepository"/> through a fresh <see cref="IServiceScopeFactory"/>-created scope
/// per lookup — the same per-lookup-scope pattern <c>InProcessSystemSettingsCache</c> and
/// <c>CachedCurrentTenantResolver</c> use to let a singleton reach scoped, DB-backed state safely.
///
/// <see cref="Current"/> starts as <see cref="LicenseStatus.Unlicensed"/> and stays that way until
/// something calls <see cref="ReloadAsync"/> — the constructor deliberately does no I/O beyond wiring up
/// the cross-replica invalidation below. Program.cs (both the Api and Worker hosts) calls
/// <see cref="ReloadAsync"/> once at startup, right after <c>AppSecretProvisioner.ProvisionAsync</c>.
///
/// <see cref="Current"/>/<see cref="CurrentRawToken"/> are per-PROCESS in-memory state — on a deployment
/// scaled to multiple replicas (e.g. Azure Container Apps' minReplicas/maxReplicas), an admin applying a
/// license through whichever replica handled that request would otherwise leave every OTHER replica
/// reporting the license it had at ITS OWN last startup/reload forever, since nothing ever told them to
/// re-check — the exact "activated for me, everyone else still sees no active license, even though
/// License History shows it applied (that table IS shared, via the DB)" symptom this closes. Same two-layer
/// fix as <c>InProcessAllowedCorsOriginsCache</c> (which already depends on the same Redis connection):
///  1. When <see cref="IConnectionMultiplexer"/> is available, <see cref="ApplyAsync"/>/<see cref="ClearAsync"/>
///     publish to a Redis channel every replica subscribes to on construction — every OTHER replica
///     re-reads the license within about a round-trip, not indefinitely.
///  2. A periodic timer re-runs <see cref="ReloadAsync"/> regardless, so every replica is never more than
///     <see cref="ReloadInterval"/> stale even if Redis is unavailable or a publish is missed — mirroring
///     that cache's MaxAge fallback, just re-polling on a timer instead of on next-read, since
///     <see cref="Current"/> is read synchronously off the hot request path and can't await a DB check.
///
/// <see cref="ReloadAsync"/> now running periodically (not just once, at startup, as it always did before
/// the timer above was added) changes what a transient database failure means. <see cref="ResolveTokenAsync"/>
/// has always tolerated a <see cref="DbException"/> reading the token — originally so the genuine
/// migrations-haven't-run-yet race at startup falls through to the env-var/file sources instead of failing
/// the whole first load. Left unchanged, that same tolerance would let a later, purely transient failure
/// (a connection reset, failover, or pool exhaustion — SqlException/NpgsqlException both derive from
/// DbException) resolve to "no token", downgrade <see cref="Current"/> to <see cref="LicenseStatus.Unlicensed"/>,
/// and 403 every <c>/api/v1</c> request behind Program.cs's license gate for up to <see cref="ReloadInterval"/>
/// on that replica — trading the cross-replica staleness bug this class fixes for an intermittent full-API
/// outage, which is worse. <see cref="ResolveTokenAsync"/> therefore reports a DB read failure distinctly
/// (<see cref="TokenResolution.DbReadFailed"/>), and <see cref="ReloadAsync"/> only tolerates it on the
/// process's first load; every later reload leaves <see cref="Current"/> untouched instead and logs a
/// warning, so a replica rides out a database blip on its last-known-good license state rather than locking
/// itself out.
///
/// This stage is verification/reporting only: nothing here blocks or gates any product behavior on the
/// resolved <see cref="LicenseStatus"/>.
/// </summary>
public sealed class LicenseService : ILicenseService, IDisposable
{
    /// <summary>SystemSetting key the currently-applied license token is persisted under.</summary>
    private const string LicenseTokenSettingKey = "License:Token";

    /// <summary>Env var fallback, checked when no SystemSetting row is present.</summary>
    private const string LicenseTokenEnvVar = "FHIRBRIDGE_LICENSE";

    /// <summary>Env var naming a file whose contents are the token — checked last.</summary>
    private const string LicenseTokenFileEnvVar = "FHIRBRIDGE_LICENSE_FILE";

    private static readonly RedisChannel InvalidationChannel = RedisChannel.Literal("fhirbridge:license-invalidated");

    // Bounds cross-replica staleness when Redis pub/sub isn't available or a publish is missed — same
    // 5-minute bound InProcessAllowedCorsOriginsCache already advertises to operators for the same class
    // of cross-replica propagation delay.
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<LicenseService> _logger;
    private readonly object _lock = new();
    // Serializes every ReloadAsync call — timer-triggered, Redis-invalidation-triggered, and Program.cs's
    // own startup call can otherwise race, and the later one to finish would win regardless of which
    // actually read newer data.
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private volatile bool _subscribedToInvalidation;
    private volatile bool _hasLoadedOnce;
    private readonly Timer _reloadTimer;
    private readonly EventHandler<ConnectionFailedEventArgs>? _onConnectionRestored;

    public LicenseService(IServiceScopeFactory scopeFactory, ILogger<LicenseService> logger, IConnectionMultiplexer? redis = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _redis = redis;

        if (_redis is not null)
        {
            // ConnectionRestored retries the subscribe so a replica that started during a Redis outage
            // still picks up cross-replica invalidation once Redis is back, rather than relying on the
            // reload timer for the rest of its life. Same reasoning as InProcessAllowedCorsOriginsCache.
            // Kept as a named field (not a lambda) so Dispose can detach it.
            _onConnectionRestored = (_, _) => TrySubscribeToInvalidation();
            _redis.ConnectionRestored += _onConnectionRestored;
            TrySubscribeToInvalidation();
        }

        // Deliberately does NOT call ReloadAsync here — the class doc's "constructor does no I/O" contract
        // stays true for the FIRST load (Program.cs controls exactly when that happens at startup). Re-armed
        // as a one-shot after each run completes (see OnReloadTimerTick) rather than a single recurring
        // interval, so a reload that runs long (e.g. a hung DB call) can never overlap itself — the interval
        // is the gap AFTER completion, not a fixed wall-clock cadence.
        _reloadTimer = new Timer(_ => OnReloadTimerTick(), null, ReloadInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnReloadTimerTick()
    {
        _ = RunAndRearmAsync();

        async Task RunAndRearmAsync()
        {
            try
            {
                await ReloadAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                // ReloadAsync's own DB-read tolerance means this should be rare (an unexpected failure
                // outside that path) — logged rather than silently swallowed, since Current gates the
                // whole API and a reload failing with no trace anywhere would be invisible otherwise.
                _logger.LogWarning(exception, "Periodic license reload failed; will retry in {ReloadInterval}.", ReloadInterval);
            }
            finally
            {
                try
                {
                    _reloadTimer.Change(ReloadInterval, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Disposed mid-flight (host shutting down) — nothing to re-arm.
                }
            }
        }
    }

    // Fire-and-forget async, not the synchronous Subscribe — see InProcessAllowedCorsOriginsCache's
    // identical method for why (a slow-but-reachable Redis must never block whichever caller constructs
    // this singleton), and why the catch is bare Exception rather than a narrower Redis-specific type.
    private void TrySubscribeToInvalidation()
    {
        if (_redis is null || _subscribedToInvalidation) return;
        _ = SubscribeAsyncCore();

        async Task SubscribeAsyncCore()
        {
            try
            {
                await _redis.GetSubscriber().SubscribeAsync(InvalidationChannel, (_, _) => _ = ReloadFromInvalidationAsync());
                _subscribedToInvalidation = true;
            }
            catch (Exception)
            {
                // Still down/hung — ConnectionRestored will call this again; the reload timer bounds
                // staleness meanwhile.
            }
        }

        async Task ReloadFromInvalidationAsync()
        {
            try
            {
                await ReloadAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "License reload triggered by cross-replica invalidation failed.");
            }
        }
    }

    public void Dispose()
    {
        _reloadTimer.Dispose();
        if (_redis is not null)
        {
            if (_onConnectionRestored is not null)
            {
                _redis.ConnectionRestored -= _onConnectionRestored;
            }
            // Fire-and-forget, same reasoning as PublishInvalidation — a hung/down Redis must never block
            // disposal, and this is a process-lifetime singleton unsubscribing on host shutdown, not a
            // correctness-critical cleanup.
            try
            {
                _ = _redis.GetSubscriber().UnsubscribeAsync(InvalidationChannel);
            }
            catch (Exception)
            {
                // Best-effort — the process is going away regardless.
            }
        }
        _reloadGate.Dispose();
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
            // one of THIS install's own past requests, proving it was minted for a request this install
            // actually made and not one copied from a different customer. A token with no requestKey
            // claim at all skips this entirely — every license minted before this feature existed, or one
            // deliberately minted without a request tied to it, keeps working unchanged.
            if (!string.IsNullOrEmpty(status.RequestKey))
            {
                var requestRepository = scope.ServiceProvider.GetRequiredService<ILicenseRequestRepository>();
                var ourRequest = await requestRepository.GetByUniqueKeyAsync(status.RequestKey, cancellationToken);
                if (ourRequest is null)
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
        PublishInvalidation();

        return new LicenseApplyResult(true, null, status);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            // Read before resolving, not after: this call's own outcome is what sets it for every call
            // after it, so the decision below must reflect whether this is the FIRST load, not whatever
            // it becomes once this call finishes.
            var isFirstLoad = !_hasLoadedOnce;
            var resolution = await ResolveTokenAsync(cancellationToken);

            if (resolution.DbReadFailed && !isFirstLoad)
            {
                // A transient DB failure (connection reset, failover, pool exhaustion) on anything after
                // the first load must NOT be treated as "no token configured" — see the class doc for why.
                // Leave Current exactly as it was; the timer/next invalidation will try again.
                _logger.LogWarning(
                    "License reload could not read the license token from the database; leaving the " +
                    "current in-memory license state unchanged rather than treating this as unlicensed.");
                return;
            }

            var status = SignedLicenseValidator.Validate(resolution.Token);

            lock (_lock)
            {
                Current = status;
                CurrentRawToken = status.State == LicenseState.Invalid ? null : resolution.Token;
            }
            _hasLoadedOnce = true;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISystemSettingRepository>();
            await repository.DeleteAsync(LicenseTokenSettingKey, cancellationToken);
        }

        lock (_lock)
        {
            Current = LicenseStatus.Unlicensed;
            CurrentRawToken = null;
        }
        PublishInvalidation();
    }

    /// <summary>Tells every OTHER replica to re-run <see cref="ReloadAsync"/> — see the class doc's
    /// cross-replica remarks. Fire-and-forget: this replica already has the fresh state in Current above,
    /// and a Redis hiccup here must never turn a successful apply/clear into a failed request — the
    /// reload timer still bounds every other replica's staleness regardless of whether this lands.</summary>
    private void PublishInvalidation() =>
        _redis?.GetSubscriber().Publish(InvalidationChannel, RedisValue.EmptyString, CommandFlags.FireAndForget);

    /// <summary><see cref="ResolveTokenAsync"/>'s result. <see cref="DbReadFailed"/> distinguishes "the
    /// database read itself failed" from "the read succeeded and there's genuinely no SystemSetting row" —
    /// <see cref="ReloadAsync"/> treats the two very differently after the first load (see the class doc).
    /// Deliberately still reports a resolved <see cref="Token"/> alongside a true <see cref="DbReadFailed"/>
    /// when the env var/file fallback found one despite the DB read failing, matching the pre-existing
    /// tolerant behavior for that combination on the first load.</summary>
    private readonly record struct TokenResolution(string? Token, bool DbReadFailed);

    /// <summary>Checks, in order: (a) the SystemSetting row, (b) <see cref="LicenseTokenEnvVar"/>, (c) the
    /// file named by <see cref="LicenseTokenFileEnvVar"/>. First one found wins.</summary>
    private async Task<TokenResolution> ResolveTokenAsync(CancellationToken cancellationToken)
    {
        SystemSetting? setting = null;
        var dbReadFailed = false;
        using (var scope = _scopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISystemSettingRepository>();
            try
            {
                setting = await repository.GetByKeyAsync(LicenseTokenSettingKey, cancellationToken);
            }
            catch (DbException)
            {
                // Tolerated only on the process's first load (ReloadAsync enforces that) — the genuine race
                // this exists for is the SystemSettings table not existing yet because this reload runs
                // during startup before migrations apply. Mirrors InProcessSystemSettingsCache's handling
                // of the same race.
                dbReadFailed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(setting?.Value))
        {
            return new TokenResolution(setting.Value, dbReadFailed);
        }

        var envToken = Environment.GetEnvironmentVariable(LicenseTokenEnvVar);
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return new TokenResolution(envToken, dbReadFailed);
        }

        var licenseFilePath = Environment.GetEnvironmentVariable(LicenseTokenFileEnvVar);
        if (!string.IsNullOrWhiteSpace(licenseFilePath) && File.Exists(licenseFilePath))
        {
            var fileContents = await File.ReadAllTextAsync(licenseFilePath, cancellationToken);
            return new TokenResolution(
                string.IsNullOrWhiteSpace(fileContents) ? null : fileContents.Trim(), dbReadFailed);
        }

        return new TokenResolution(null, dbReadFailed);
    }
}
