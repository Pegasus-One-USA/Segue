using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Security;

public sealed class LocalIdentitySeedService : IIdentitySeedService
{
    private readonly IUserAccessRepository _repository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IConfiguration _configuration;

    public LocalIdentitySeedService(
        IUserAccessRepository repository,
        IPasswordHasher passwordHasher,
        IConfiguration configuration)
    {
        _repository = repository;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(_configuration["LocalAuth:SeedAdmin:Email"] ?? "admin@fhirbridge.local");
        var password = _configuration["LocalAuth:SeedAdmin:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var displayName = _configuration["LocalAuth:SeedAdmin:DisplayName"] ?? "FHIRBridge Super Admin";
        var user = await _repository.GetUserByEmailAsync(email, cancellationToken);

        if (user is null)
        {
            user = new User(LocalExternalId(email), email, displayName);
            user.EnableLocalLogin(
                _passwordHasher.Hash(password),
                bool.TryParse(_configuration["LocalAuth:SeedAdmin:RequirePasswordChange"], out var requireChange) && requireChange);

            await _repository.AddUserAsync(user, cancellationToken);
        }
        else
        {
            user.UpdateProfile(email, displayName);
            if (!user.IsLocalLoginEnabled || string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                user.EnableLocalLogin(_passwordHasher.Hash(password), mustChangePassword: true);
            }

            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        var globalAdminRole = await _repository.GetRoleByNameAsync(UnifiedRoles.GlobalAdmin, cancellationToken)
            ?? throw new InvalidOperationException("GlobalAdmin role seed is missing.");

        var currentRoles = await _repository.GetUserRolesAsync(user.Id, cancellationToken);
        var roleIds = currentRoles.Select(x => x.Id).Append(globalAdminRole.Id).Distinct().ToArray();
        await _repository.SetUserRolesAsync(user.Id, roleIds, cancellationToken);
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static string LocalExternalId(string email)
    {
        return $"local:{email}";
    }
}
