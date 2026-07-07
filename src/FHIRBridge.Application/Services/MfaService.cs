using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Application.Services;

public sealed class MfaService : IMfaService
{
    private readonly IUserAccessRepository _repository;
    private readonly ITotpService _totpService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserActivityAuditService _activityAuditService;
    private readonly MfaOptions _options;

    public MfaService(
        IUserAccessRepository repository,
        ITotpService totpService,
        IPasswordHasher passwordHasher,
        ICurrentUserService currentUserService,
        IUserActivityAuditService activityAuditService,
        IOptions<MfaOptions> options)
    {
        _repository = repository;
        _totpService = totpService;
        _passwordHasher = passwordHasher;
        _currentUserService = currentUserService;
        _activityAuditService = activityAuditService;
        _options = options.Value;
    }

    public async Task<MfaStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);
        var remaining = string.IsNullOrEmpty(user.MfaBackupCodeHashes)
            ? 0
            : user.MfaBackupCodeHashes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        return new MfaStatusResponse(user.MfaEnabled, user.MfaEnrolledOnUtc, remaining);
    }

    public async Task<MfaEnrollmentResponse> BeginEnrollmentAsync(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);

        var secret = _totpService.GenerateSecret();
        user.BeginMfaEnrollment(secret);
        await _repository.UpdateUserAsync(user, cancellationToken);

        var accountName = user.Email ?? user.ExternalUserId;
        var uri = _totpService.BuildProvisioningUri(secret, accountName, _options.Issuer);

        return new MfaEnrollmentResponse(secret, uri);
    }

    public async Task<MfaEnrollmentConfirmedResponse> ConfirmEnrollmentAsync(
        MfaCodeRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(user.MfaSecret))
        {
            throw new InvalidOperationException("MFA enrollment has not been started.");
        }

        if (!_totpService.ValidateCode(user.MfaSecret, request.Code))
        {
            await RecordAsync(user.Id, user.Email, "MfaEnrollFailed", UserActivityStatuses.Failed,
                UserActivitySeverities.Warning, "Invalid TOTP code during enrollment.", cancellationToken);
            throw new InvalidOperationException("The verification code is invalid.");
        }

        var backupCodes = MfaBackupCodes.Generate(_options.BackupCodeCount);
        var hashes = backupCodes.Select(code => _passwordHasher.Hash(code.ToUpperInvariant()));
        user.ConfirmMfaEnrollment(hashes);
        await _repository.UpdateUserAsync(user, cancellationToken);

        await RecordAsync(user.Id, user.Email, "MfaEnabled", UserActivityStatuses.Success,
            UserActivitySeverities.Information, null, cancellationToken);

        return new MfaEnrollmentConfirmedResponse(backupCodes);
    }

    public async Task DisableAsync(MfaCodeRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync(cancellationToken);

        if (!user.MfaEnabled)
        {
            return;
        }

        var codeValid = (!string.IsNullOrWhiteSpace(user.MfaSecret) &&
                         _totpService.ValidateCode(user.MfaSecret, request.Code)) ||
                        MfaBackupCodes.TryConsume(user, request.Code, _passwordHasher);

        if (!codeValid)
        {
            await RecordAsync(user.Id, user.Email, "MfaDisableFailed", UserActivityStatuses.Denied,
                UserActivitySeverities.Warning, "Invalid code when attempting to disable MFA.", cancellationToken);
            throw new InvalidOperationException("A valid MFA code is required to disable MFA.");
        }

        user.DisableMfa();
        await _repository.UpdateUserAsync(user, cancellationToken);

        await RecordAsync(user.Id, user.Email, "MfaDisabled", UserActivityStatuses.Success,
            UserActivitySeverities.Warning, null, cancellationToken);
    }

    private async Task<Domain.Entities.User> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var externalUserId = _currentUserService.CurrentUser.ExternalUserId;
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            throw new InvalidOperationException("Authenticated user id claim is missing.");
        }

        return await _repository.GetUserByExternalIdAsync(externalUserId, cancellationToken)
            ?? throw new InvalidOperationException("User was not found.");
    }

    private Task RecordAsync(
        Guid userId,
        string? email,
        string activity,
        string status,
        string severity,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        return _activityAuditService.RecordAsync(
            new RecordUserActivityRequest(
                UserId: userId,
                UserEmail: email ?? _currentUserService.CurrentUser.AuditName,
                Category: UserActivityCategories.Authentication,
                Activity: activity,
                Status: status,
                EntityName: nameof(Domain.Entities.User),
                EntityId: userId,
                FailureReason: failureReason,
                Severity: severity),
            cancellationToken);
    }
}
