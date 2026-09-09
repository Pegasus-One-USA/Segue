using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Destinations.Auth;

/// <summary>
/// Resolves the optional <c>Authorization</c> header for a <c>DestinationType.FhirRepository</c> destination, shared
/// by <see cref="MappedFhirRepositoryDestinationWriter"/> and <see cref="FhirRepositoryHealthCheckProvider"/> so the
/// discriminator-parsing/secret-shape logic exists in exactly one place. Reads the non-secret
/// <c>dest_fhirAuthType</c> flag via the existing <see cref="ConnectionMetadataReader"/> convention:
/// <list type="bullet">
/// <item><c>absent</c> or <c>"none"</c> — every <c>FhirRepository</c> row today — returns null immediately; the
/// secret is never touched, matching today's exact behavior byte-for-byte.</item>
/// <item><c>"bearer"</c> — the Key Vault secret is a JSON object <c>{ "token": "..." }</c>.</item>
/// <item><c>"basic"</c> — the Key Vault secret is a JSON object <c>{ "username": "...", "password": "..." }</c>,
/// sent as a standard HTTP Basic <c>Authorization</c> header.</item>
/// <item><c>"clientCredentials"</c> — the Key Vault secret is a JSON object
/// <c>{ "clientId": "...", "clientSecret": "...", "tokenEndpoint": "...", "scope": "..." }</c> (scope optional),
/// mirroring the multi-field secret-blob convention <c>MappedDatabricksDestinationWriter</c> already uses.</item>
/// </list>
/// </summary>
internal static class FhirRepositoryAuthResolver
{
    public static async Task<AuthenticationHeaderValue?> ResolveAsync(
        string? connectionMetadataJson,
        SecretReference secretReference,
        ISecretProvider secretProvider,
        IFhirDestinationTokenProvider tokenProvider,
        IAzureManagedIdentityFhirTokenProvider managedIdentityTokenProvider,
        string? baseUrl,
        CancellationToken cancellationToken)
    {
        var authType = ConnectionMetadataReader.GetString(connectionMetadataJson, "dest_fhirAuthType") ?? "none";
        if (string.Equals(authType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Managed identity never resolves a Key Vault secret — the scope/authority/user-assigned-identity-id it
        // needs are all non-secret connection metadata, same as MappedFhirRepositoryDestinationWriter's other flags.
        if (string.Equals(authType, "managedIdentity", StringComparison.OrdinalIgnoreCase))
        {
            var scope = ConnectionMetadataReader.GetString(connectionMetadataJson, "dest_fhirAzureScope");
            if (string.IsNullOrWhiteSpace(scope))
            {
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    throw new InvalidOperationException(
                        "dest_fhirAuthType 'managedIdentity' requires either dest_fhirAzureScope or a FHIR base URL to derive the default scope from.");
                }

                scope = $"{baseUrl.TrimEnd('/')}/.default";
            }

            var managedIdentityClientId = ConnectionMetadataReader.GetString(connectionMetadataJson, "dest_fhirManagedIdentityClientId");
            var authorityHost = ConnectionMetadataReader.GetString(connectionMetadataJson, "dest_fhirAuthorityHost");
            var token = await managedIdentityTokenProvider.GetAccessTokenAsync(
                scope, managedIdentityClientId, authorityHost, cancellationToken);
            return new AuthenticationHeaderValue("Bearer", token);
        }

        var secretJson = await secretProvider.GetSecretAsync(secretReference, cancellationToken);
        var secret = FhirRepositoryAuthSecret.Parse(secretJson, authType);

        return authType.ToLowerInvariant() switch
        {
            "bearer" => new AuthenticationHeaderValue("Bearer", secret.Token),
            "basic" => new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{secret.Username}:{secret.Password}"))),
            "clientcredentials" => new AuthenticationHeaderValue(
                "Bearer",
                await tokenProvider.GetAccessTokenAsync(
                    new FhirDestinationOAuth2Options(secret.TokenEndpoint!, secret.ClientId!, secret.ClientSecret!, secret.Scope),
                    cancellationToken)),
            _ => throw new InvalidOperationException(
                $"Unsupported dest_fhirAuthType '{authType}' for a FHIR repository destination. Supported: none, bearer, basic, clientCredentials, managedIdentity.")
        };
    }

    // Internal (not private) so FhirDestinationConnectionTestService can reuse the exact same secret-blob shape
    // when re-testing an already-saved destination via DestinationId, instead of duplicating this parsing logic.
    internal sealed class FhirRepositoryAuthSecret
    {
        [JsonPropertyName("token")]
        public string? Token { get; init; }

        [JsonPropertyName("username")]
        public string? Username { get; init; }

        [JsonPropertyName("password")]
        public string? Password { get; init; }

        [JsonPropertyName("clientId")]
        public string? ClientId { get; init; }

        [JsonPropertyName("clientSecret")]
        public string? ClientSecret { get; init; }

        [JsonPropertyName("tokenEndpoint")]
        public string? TokenEndpoint { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }

        public static FhirRepositoryAuthSecret Parse(string json, string authType)
        {
            FhirRepositoryAuthSecret? secret;
            try
            {
                secret = JsonSerializer.Deserialize<FhirRepositoryAuthSecret>(json);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("FHIR repository auth secret must be a JSON object.", exception);
            }

            if (secret is null)
            {
                throw new InvalidOperationException("FHIR repository auth secret must be a JSON object.");
            }

            if (string.Equals(authType, "bearer", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(secret.Token))
            {
                throw new InvalidOperationException("dest_fhirAuthType 'bearer' requires a 'token' field in the secret.");
            }

            if (string.Equals(authType, "basic", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(secret.Username) || string.IsNullOrWhiteSpace(secret.Password)))
            {
                throw new InvalidOperationException("dest_fhirAuthType 'basic' requires 'username' and 'password' fields in the secret.");
            }

            if (string.Equals(authType, "clientCredentials", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(secret.ClientId)
                    || string.IsNullOrWhiteSpace(secret.ClientSecret)
                    || string.IsNullOrWhiteSpace(secret.TokenEndpoint)))
            {
                throw new InvalidOperationException(
                    "dest_fhirAuthType 'clientCredentials' requires 'clientId', 'clientSecret', and 'tokenEndpoint' in the secret.");
            }

            return secret;
        }
    }
}
