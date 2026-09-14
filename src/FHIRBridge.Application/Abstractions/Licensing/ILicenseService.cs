namespace FHIRBridge.Application.Abstractions.Licensing;

/// <summary>
/// Result of applying a new license token via <see cref="ILicenseService.ApplyAsync"/>. On failure
/// (<see cref="Succeeded"/> is <c>false</c>), nothing was persisted and <see cref="ILicenseService.Current"/>
/// is left unchanged — see <c>LicenseService.ApplyAsync</c>'s remarks ("a bad token is rejected at the
/// door rather than bricking the next restart").
/// </summary>
public sealed record LicenseApplyResult(bool Succeeded, string? ErrorMessage, LicenseStatus? Status);

/// <summary>
/// Resolves, verifies, and reports the product's current signed license. Registered as a singleton by
/// FHIRBridge.Infrastructure (see <c>LicenseService</c>) so <see cref="Current"/> is a cheap in-memory read
/// on every request without re-parsing/re-verifying the token each time.
///
/// This is a verification/reporting surface only — this stage does not block or gate any product
/// behavior on the returned <see cref="LicenseStatus"/>. A later stage is expected to layer quota
/// enforcement and usage tracking on top of exactly this interface.
/// </summary>
public interface ILicenseService
{
    /// <summary>The most recently resolved-and-verified license snapshot. Never null —
    /// <see cref="LicenseStatus.Unlicensed"/> when no token could be resolved from any source.</summary>
    LicenseStatus Current { get; }

    /// <summary>The raw signed JWS string that produced <see cref="Current"/> — <c>null</c> whenever
    /// <see cref="Current"/> is <see cref="LicenseState.Invalid"/> or no token could be resolved from any
    /// source. Exists so a caller (e.g. <c>LicenseHeartbeatWorker</c>) can forward the exact token this
    /// install currently has applied to a remote verifier without re-deriving or re-serializing it — the
    /// remote side verifies the token's signature itself, so only the original raw string is useful to it.</summary>
    string? CurrentRawToken { get; }

    /// <summary>Whether <see cref="Current"/> grants the given feature key (see <see cref="LicenseFeatures"/>),
    /// case-insensitively. Always <c>false</c> when <see cref="Current"/> is not
    /// <see cref="LicenseState.Active"/>... except this stage does not enforce that distinction either; it
    /// simply reflects whatever is in <see cref="LicenseStatus.Features"/> regardless of state, leaving the
    /// enforcement decision (e.g. "only trust HasFeature when State == Active") to the calling stage.</summary>
    bool HasFeature(string featureKey);

    /// <summary>Verifies <paramref name="licenseToken"/> and, only on success, persists it (as the new
    /// <c>SystemSetting["License:Token"]</c> row) and updates <see cref="Current"/>. A signature-invalid,
    /// malformed, or wrong-issuer token is rejected before anything is written.</summary>
    Task<LicenseApplyResult> ApplyAsync(string licenseToken, CancellationToken cancellationToken);

    /// <summary>Re-runs the same source-resolution-and-verification flow used at startup, refreshing
    /// <see cref="Current"/> from whichever source (DB setting / env var / file) currently wins.</summary>
    Task ReloadAsync(CancellationToken cancellationToken);
}
