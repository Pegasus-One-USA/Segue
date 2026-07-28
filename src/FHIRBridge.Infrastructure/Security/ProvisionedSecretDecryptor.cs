using FHIRBridge.Application.Abstractions.Security;
using Microsoft.AspNetCore.DataProtection;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Same protector (purpose <c>"FHIRBridge.Secrets.v1"</c>) as <see cref="DbSecretStore"/>, so this decrypts
/// exactly the values that store wrote — nothing else.
/// </summary>
public sealed class ProvisionedSecretDecryptor : IProvisionedSecretDecryptor
{
    private const string ProtectorPurpose = "FHIRBridge.Secrets.v1";

    private readonly IDataProtector _protector;

    public ProvisionedSecretDecryptor(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public string Decrypt(string protectedValue) => _protector.Unprotect(protectedValue);
}
