using System.Security.Cryptography;

namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Generates the random value used for app-level secrets (see <see cref="IAppSecretAccessor"/>) — shared by
/// first-boot generation (<c>AppSecretProvisioner</c>) and on-demand regeneration (<c>AppSecretsAdminService</c>)
/// so both produce values of the same strength.
/// </summary>
public static class AppSecretValueGenerator
{
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
