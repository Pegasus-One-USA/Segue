using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

/// <summary>
/// SuperAdmin-facing management of the app-level signing secrets (see <see cref="IAppSecretAccessor"/>):
/// lists provisioning status and supports on-demand regeneration. Write-only by design — the value itself
/// is never read back; regeneration only ever produces a fresh, never-before-seen value.
/// </summary>
public sealed class AppSecretsAdminService : IAppSecretsAdminService
{
    private sealed record CatalogEntry(
        string SecretName, string DisplayName, SecretReference Reference, bool RestartRequiredForFullEffect);

    // The download-link secret can also be minted/verified by the Worker process, which caches its own copy
    // independently — regenerating it here updates only the Api's in-process cache immediately; the Worker
    // won't see the new value until it restarts, so links it mints meanwhile won't verify against it. The JWT
    // signing key is Api-only (the Worker never issues/validates tokens), so it has no such gap.
    private static readonly IReadOnlyList<CatalogEntry> Catalog =
    [
        new("jwt-signing-key", "JWT Signing Key", AppSecretReferences.JwtSigningKey, RestartRequiredForFullEffect: false),
        new("download-link-signing-secret", "Download-Link Signing Secret", AppSecretReferences.DownloadLinkSigningSecret, RestartRequiredForFullEffect: true),
    ];

    private readonly ISecretWriter _secretWriter;
    private readonly IAppSecretMetadataProvider _metadataProvider;
    private readonly IAppSecretAccessor _secretAccessor;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ICurrentUserService _currentUserService;

    public AppSecretsAdminService(
        ISecretWriter secretWriter,
        IAppSecretMetadataProvider metadataProvider,
        IAppSecretAccessor secretAccessor,
        IGovernanceLogger governanceLogger,
        ICurrentUserService currentUserService)
    {
        _secretWriter = secretWriter;
        _metadataProvider = metadataProvider;
        _secretAccessor = secretAccessor;
        _governanceLogger = governanceLogger;
        _currentUserService = currentUserService;
    }

    public async Task<IReadOnlyList<AppSecretDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var results = new List<AppSecretDto>(Catalog.Count);
        foreach (var entry in Catalog)
        {
            var metadata = await _metadataProvider.GetMetadataAsync(entry.Reference, cancellationToken);
            results.Add(ToDto(entry, metadata));
        }

        return results;
    }

    public async Task<AppSecretDto> RegenerateAsync(string secretName, CancellationToken cancellationToken)
    {
        var entry = Catalog.FirstOrDefault(c => c.SecretName == secretName)
            ?? throw new NotFoundException(nameof(AppSecretDto), secretName);

        var newValue = AppSecretValueGenerator.Generate();
        await _secretWriter.WriteSecretAsync(entry.Reference, newValue, cancellationToken);
        _secretAccessor.Update(entry.Reference, newValue);

        await _governanceLogger.LogSecurityEventAsync(
            new SecurityEventEntry(
                "AppSecretRegenerated",
                "High",
                _currentUserService.CurrentUser.Email,
                $"'{entry.DisplayName}' was regenerated. Every token/link signed with the previous value is now invalid."),
            cancellationToken);

        var metadata = await _metadataProvider.GetMetadataAsync(entry.Reference, cancellationToken);
        return ToDto(entry, metadata);
    }

    private static AppSecretDto ToDto(CatalogEntry entry, ProvisionedSecretMetadata metadata) =>
        new(entry.SecretName, entry.DisplayName, metadata.Provisioned, metadata.LastRotatedUtc, entry.RestartRequiredForFullEffect);
}
