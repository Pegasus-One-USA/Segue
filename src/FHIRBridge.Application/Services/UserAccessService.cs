using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public sealed class UserAccessService : IUserAccessService
{
    private readonly IUserAccessRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IOperationalAuditService _auditService;

    public UserAccessService(
        IUserAccessRepository repository,
        ICurrentUserService currentUserService,
        IOperationalAuditService auditService)
    {
        _repository = repository;
        _currentUserService = currentUserService;
        _auditService = auditService;
    }

    public async Task<UserProfileDto> GetCurrentUserProfileAsync(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(recordLogin: false, cancellationToken);

        return await ToProfileDtoAsync(user, cancellationToken);
    }

    public async Task<UserProfileDto> RecordLoginAsync(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(recordLogin: true, cancellationToken);

        await _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                null,
                null,
                null,
                null,
                null,
                null,
                "UserLogin",
                "Completed",
                "FHIRBridge portal login observed.",
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);

        return await ToProfileDtoAsync(user, cancellationToken);
    }

    private async Task<User> GetOrCreateCurrentUserAsync(
        bool recordLogin,
        CancellationToken cancellationToken)
    {
        var currentUser = _currentUserService.CurrentUser;
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
        {
            throw new InvalidOperationException("Authenticated user id claim is missing.");
        }

        var user = await _repository.GetUserByExternalIdAsync(currentUser.ExternalUserId, cancellationToken);
        if (user is null)
        {
            user = new User(currentUser.ExternalUserId, currentUser.Email, currentUser.DisplayName);
            await _repository.AddUserAsync(user, cancellationToken);
        }
        else
        {
            user.UpdateProfile(currentUser.Email, currentUser.DisplayName);
            if (recordLogin)
            {
                user.RecordLogin();
            }

            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        if (recordLogin && user.LastLoginOnUtc is null)
        {
            user.RecordLogin();
            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        return user;
    }

    private Task<UserProfileDto> ToProfileDtoAsync(
        User user,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new UserProfileDto(
            user.Id,
            user.ExternalUserId,
            user.Email,
            user.DisplayName,
            _currentUserService.CurrentUser.Roles));
    }
}
