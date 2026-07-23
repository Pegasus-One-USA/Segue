using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// Global (non-tenant-scoped) outbound email/SMTP configuration — a single row, admin-editable from the Settings
/// hub instead of appsettings.json. Mirrors <see cref="DestinationConfiguration"/>'s split of plain connection
/// fields vs. a <see cref="SecretReference"/> for the one secret (the SMTP password), so the password is never
/// stored inline and resolves through the same secret-store path as every other credential in the system.
/// </summary>
public sealed class NotificationSettings : AuditableChildEntity<Guid>
{
    private NotificationSettings()
    {
    }

    public NotificationSettings(
        bool isEnabled,
        string host,
        int port,
        bool enableSsl,
        string username,
        string fromAddress,
        string fromName,
        SecretReference? passwordSecretReference)
    {
        Id = Guid.NewGuid();
        IsEnabled = isEnabled;
        Host = host;
        Port = port;
        EnableSsl = enableSsl;
        Username = username;
        FromAddress = fromAddress;
        FromName = fromName;
        PasswordSecretReference = passwordSecretReference;
    }

    public bool IsEnabled { get; private set; }
    public string Host { get; private set; } = string.Empty;
    public int Port { get; private set; } = 587;
    public bool EnableSsl { get; private set; } = true;
    public string Username { get; private set; } = string.Empty;
    public string FromAddress { get; private set; } = string.Empty;
    public string FromName { get; private set; } = "FHIRBridge";
    public SecretReference? PasswordSecretReference { get; private set; }

    public void Update(
        bool isEnabled,
        string host,
        int port,
        bool enableSsl,
        string username,
        string fromAddress,
        string fromName,
        SecretReference? passwordSecretReference)
    {
        IsEnabled = isEnabled;
        Host = host;
        Port = port;
        EnableSsl = enableSsl;
        Username = username;
        FromAddress = fromAddress;
        FromName = fromName;
        PasswordSecretReference = passwordSecretReference;
    }
}
