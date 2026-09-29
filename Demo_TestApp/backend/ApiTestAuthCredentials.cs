namespace HealthAppBackend;

/// <summary>
/// Fixed, well-known test credentials for every auth mode <see cref="ApiAuthTestEndpoints"/> validates — mirroring
/// the set FHIRBridge's own <c>ApiEndpoint</c> destination supports (see <c>ApiEndpointAuthMode</c> in
/// <c>FHIRBridge.Infrastructure</c>). These are deliberately hardcoded and published in the console's docs tab
/// rather than configured: unlike <see cref="DataLakeWebhookEndpoints"/> (one active auth mode at a time, set via
/// config, requiring a restart to switch), every mode here needs to be exercisable simultaneously — a tester
/// pointing several destination rows at several of these endpoints in the same run. There is nothing behind these
/// endpoints worth protecting for real, so a fixed, openly-documented credential per mode is the right tradeoff.
/// </summary>
public static class ApiTestAuthCredentials
{
    public const string BearerToken = "fhirbridge-test-bearer-8f2c91a4d6e7";

    public const string ApiKeyHeaderName = "X-Api-Key";
    public const string ApiKeyHeaderValue = "fhirbridge-test-apikey-3b7a05c9f1e2";

    public const string ApiKeyQueryParamName = "api_key";
    public const string ApiKeyQueryValue = "fhirbridge-test-apikey-query-6d4e18b0a7c3";

    public const string BasicUsername = "fhirbridge-test";
    public const string BasicPassword = "test-password-2c8f4a91";

    /// <summary>Shared secret for the HMAC-SHA256 signature — same convention as
    /// <see cref="ApiAuthTestEndpoints"/>'s HmacSha256 mode: signs "{unix timestamp}.{body}", sent as
    /// X-Signature-256: sha256=&lt;hex&gt; alongside X-Signature-Timestamp.</summary>
    public const string HmacSharedSecret = "fhirbridge-test-hmac-secret-9e1d7c4b2a68";
    public const string HmacSignatureHeaderName = "X-Signature-256";
    public const string HmacTimestampHeaderName = "X-Signature-Timestamp";

    /// <summary>Client credentials accepted by <c>POST /api/apitest/oauth/token</c>. The token it issues is what
    /// the OAuth2ClientCredentials mode's endpoint then validates — see <see cref="ApiTestOAuth2TokenStore"/>.</summary>
    public const string OAuth2ClientId = "fhirbridge-test-client";
    public const string OAuth2ClientSecret = "fhirbridge-test-client-secret-4f6b8d2e";
    public const int OAuth2TokenLifetimeSeconds = 3600;

    /// <summary>Fixed password on the self-signed PFX <see cref="ApiTestClientCertificateStore"/> mints at
    /// startup — paired with its base64 PFX as "&lt;pfxBase64&gt;|&lt;password&gt;", exactly the secret format
    /// ApiEndpointSender's ClientCertificate mode expects.</summary>
    public const string ClientCertificatePfxPassword = "test-cert-password";
}
