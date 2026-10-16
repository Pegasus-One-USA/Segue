using System.Security.Cryptography;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FHIRBridge.Infrastructure.Cds.Coverage;

/// <summary>
/// Segue's own CDS signing key, used when Segue calls a payer's CRD service as an intermediary (RS384, 2048-bit RSA).
/// Generated on first use; the private key (PKCS#8 PEM) is written through <see cref="ISecretWriter"/> into the
/// "signing-keys" vault (DbSecretStore / Key Vault, like the SMART Backend Services keys), and only a reference to it
/// (vault, secret name, kid) is kept, in the System Setting <see cref="SettingKey"/>. The public half is published
/// at GET /cds-services/.well-known/jwks.json, the jku of every relayed call. The private key never leaves here.
/// </summary>
public sealed class CdsRelaySigningKeyProvider : FHIRBridge.Application.Abstractions.Cds.ICdsRelayJwksProvider
{
    public const string SettingKey = "Cds:RelaySigningKey";
    public const string Algorithm = SecurityAlgorithms.RsaSha384;
    private const string VaultName = "signing-keys";
    private const int KeySizeBits = 2048;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CdsRelaySigningKeyProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RsaSecurityKey? _key;

    public CdsRelaySigningKeyProvider(IServiceScopeFactory scopeFactory, ILogger<CdsRelaySigningKeyProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>The signing key (with its kid). Created and stored on first use.</summary>
    /// <exception cref="InvalidOperationException">A key is registered but its secret cannot be read.</exception>
    public async Task<RsaSecurityKey> GetSigningKeyAsync(CancellationToken cancellationToken)
    {
        if (_key is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_key is { } again)
            {
                return again;
            }

            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingRepository>();
            var stored = await settings.GetByKeyAsync(SettingKey, cancellationToken);
            var reference = stored is null ? null : Parse(stored.Value);
            if (reference is not null)
            {
                _key = await LoadAsync(scope.ServiceProvider, reference, cancellationToken);
                return _key;
            }

            _key = await CreateAsync(scope.ServiceProvider, settings, cancellationToken);
            return _key;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<RsaSecurityKey> LoadAsync(IServiceProvider services, KeyReference reference, CancellationToken cancellationToken)
    {
        var pem = await services.GetRequiredService<ISecretProvider>()
            .GetSecretAsync(new SecretReference(reference.Vault, reference.Secret), cancellationToken);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            rsa.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
            // Never replaced silently: payers have this key's kid cached, and a new key would break them.
            throw new InvalidOperationException(
                $"The CDS relay signing key '{reference.Secret}' in '{reference.Vault}' is not a usable RSA private key. " +
                $"Delete the System Setting '{SettingKey}' to have Segue create a new key, then tell each payer to refresh Segue's JWKS.",
                exception);
        }

        return new RsaSecurityKey(rsa) { KeyId = reference.Kid };
    }

    /// <summary>The public JWKS (one RS384 key) payers fetch through the relayed calls' jku.</summary>
    public async Task<JsonWebKeySetDto> GetJwksAsync(CancellationToken cancellationToken)
    {
        var key = await GetSigningKeyAsync(cancellationToken);
        var parameters = key.Rsa.ExportParameters(includePrivateParameters: false);
        return new JsonWebKeySetDto(
        [
            new JsonWebKeyDto("RSA", "sig", "RS384", key.KeyId, Base64UrlEncoder.Encode(parameters.Modulus!), Base64UrlEncoder.Encode(parameters.Exponent!)),
        ]);
    }

    private async Task<RsaSecurityKey> CreateAsync(IServiceProvider services, ISystemSettingRepository settings, CancellationToken cancellationToken)
    {
        var rsa = RSA.Create(KeySizeBits);
        var kid = $"segue-cds-{Guid.NewGuid():N}"[..22];
        var vault = services.GetRequiredService<ITenantSecretVaultResolver>().ResolveVaultName(VaultName);
        var secret = $"cds-relay-private-key-{Guid.NewGuid():N}";
        await services.GetRequiredService<ISecretWriter>()
            .WriteSecretAsync(new SecretReference(vault, secret), rsa.ExportPkcs8PrivateKeyPem(), cancellationToken);
        var value = JsonSerializer.Serialize(new KeyReference(vault, secret, kid));

        // Insert-if-absent on the setting's unique key: when two Segue instances create a key at the same moment,
        // exactly one reference is stored and every instance signs with (and publishes) that one key.
        var stored = await settings.AddIfAbsentAsync(
            SettingKey,
            value,
            "Reference to Segue's CDS relay signing key (the private key itself is in the secret store). Do not edit.",
            cancellationToken);
        services.GetService<ISystemSettingsCache>()?.Invalidate();
        if (!string.Equals(stored.Value, value, StringComparison.Ordinal))
        {
            rsa.Dispose();
            var winner = Parse(stored.Value)
                ?? throw new InvalidOperationException($"The System Setting '{SettingKey}' is not a valid CDS relay signing key reference.");
            _logger.LogInformation(
                "Another Segue instance created the CDS relay signing key {KeyId} first; using it. The unused secret {SecretName} in {VaultName} can be deleted.",
                winner.Kid, secret, vault);
            return await LoadAsync(services, winner, cancellationToken);
        }

        _logger.LogInformation("Created the CDS relay signing key {KeyId}; private key stored as {SecretName} in {VaultName}.", kid, secret, vault);
        return new RsaSecurityKey(rsa) { KeyId = kid };
    }

    private static KeyReference? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var reference = JsonSerializer.Deserialize<KeyReference>(value);
            return reference is { Vault.Length: > 0, Secret.Length: > 0, Kid.Length: > 0 } ? reference : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record KeyReference(string Vault, string Secret, string Kid);
}
