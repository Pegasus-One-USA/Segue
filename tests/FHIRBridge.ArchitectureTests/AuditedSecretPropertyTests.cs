using System.Reflection;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.Entities;
using FHIRBridge.SharedKernel.Abstractions;
using FluentAssertions;

namespace FHIRBridge.ArchitectureTests;

/// <summary>
/// Every <see cref="IAuditableEntity"/> change is copied column by column into the append-only, never-purged
/// <c>AuditLogs</c> table by <c>AuditingSaveChangesInterceptor</c>. A credential column added without
/// <see cref="AuditRedactAttribute"/> or <see cref="AuditIgnoreAttribute"/> therefore lands there in plain text
/// (as <c>User.MfaSecret</c> and <c>User.PasswordHash</c> once did). This fails the build until the new column is
/// classified.
/// </summary>
public sealed partial class AuditedSecretPropertyTests
{
    /// <summary>Names that match the credential pattern but hold no credential. Each needs a reason.</summary>
    private static readonly Dictionary<string, string> KnownSafe = new(StringComparer.Ordinal)
    {
        ["ProvisionedSecret.SecretName"] = "the name of the secret in its vault, not its value",
        ["LicenseHistoryEntry.Token"] = "the signed license artifact as issued to the customer; documented as not a secret",
    };

    [GeneratedRegex("Secret|Password|Token|Hash", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialName();

    [Fact]
    public void Credential_like_string_columns_on_audited_entities_are_redacted_or_ignored()
    {
        var auditedTypes = new[] { typeof(User).Assembly, typeof(FHIRBridge.Runtime.Domain.Workflows.WorkflowDefinition).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IAuditableEntity).IsAssignableFrom(type));

        var unclassified = auditedTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(string)
                    && property.CanWrite
                    && CredentialName().IsMatch(property.Name))
                .Select(property => (Key: $"{type.Name}.{property.Name}", Property: property)))
            .Where(x => !KnownSafe.ContainsKey(x.Key)
                && !x.Property.IsDefined(typeof(AuditRedactAttribute), inherit: true)
                && !x.Property.IsDefined(typeof(AuditIgnoreAttribute), inherit: true))
            .Select(x => x.Key)
            .ToList();

        unclassified.Should().BeEmpty(
            "a credential column on an audited entity must carry [AuditRedact] or [AuditIgnore], or be listed in "
            + "KnownSafe with a reason; otherwise its value is copied into AuditLogs");
    }

    [Fact]
    public void The_known_user_credentials_are_never_copied_in_plain_text()
    {
        // Pins the specific columns that leaked, independent of the name pattern above.
        string[] redactedOrIgnored =
        [
            nameof(User.MfaSecret),
            nameof(User.PasswordHash),
            nameof(User.MfaBackupCodeHashes),
            nameof(User.PasswordResetTokenHash),
            nameof(User.InvitationTokenHash),
            nameof(User.RefreshTokenHash),
            nameof(User.MfaChallengeTokenHash),
            nameof(User.MagicLinkTokenHash),
        ];

        foreach (var name in redactedOrIgnored)
        {
            var property = typeof(User).GetProperty(name)!;
            (property.IsDefined(typeof(AuditRedactAttribute)) || property.IsDefined(typeof(AuditIgnoreAttribute)))
                .Should().BeTrue($"User.{name} must never reach AuditLogs in plain text");
        }
    }
}
