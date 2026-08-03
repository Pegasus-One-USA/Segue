using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Services;

public sealed class NotificationSettingsService : INotificationSettingsService
{
    // Fixed reference for the one password this entity ever stores — mirrors AppSecretReferences' fixed-name
    // convention for app-level singleton secrets, rather than exposing a Key Vault name/secret name picker to the
    // admin the way DestinationConfiguration does for customer-managed destinations.
    private static readonly SecretReference PasswordReference = new("app", "smtp-password");

    private readonly INotificationSettingsRepository _repository;
    private readonly ISecretWriter _secretWriter;

    public NotificationSettingsService(INotificationSettingsRepository repository, ISecretWriter secretWriter)
    {
        _repository = repository;
        _secretWriter = secretWriter;
    }

    public async Task<NotificationSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        return ToDto(settings);
    }

    public async Task<NotificationSettingsDto> UpdateAsync(
        UpdateNotificationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        var passwordReference = settings?.PasswordSecretReference;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            passwordReference = PasswordReference;
            await _secretWriter.WriteSecretAsync(PasswordReference, request.Password, cancellationToken);
        }

        if (settings is null)
        {
            settings = new NotificationSettings(
                request.IsEnabled,
                request.Host,
                request.Port,
                request.EnableSsl,
                request.Username,
                request.FromAddress,
                request.FromName,
                passwordReference);
        }
        else
        {
            settings.Update(
                request.IsEnabled,
                request.Host,
                request.Port,
                request.EnableSsl,
                request.Username,
                request.FromAddress,
                request.FromName,
                passwordReference);
        }

        await _repository.SaveAsync(settings, cancellationToken);

        return ToDto(settings);
    }

    private static NotificationSettingsDto ToDto(NotificationSettings? settings) =>
        settings is null
            ? new NotificationSettingsDto(false, string.Empty, 587, true, string.Empty, string.Empty, "FHIRBridge", false)
            : new NotificationSettingsDto(
                settings.IsEnabled,
                settings.Host,
                settings.Port,
                settings.EnableSsl,
                settings.Username,
                settings.FromAddress,
                settings.FromName,
                settings.PasswordSecretReference is not null);
}
