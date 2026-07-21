using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>Outbound HTTP connection-pool tuning, applied per named FHIR HttpClient (multi-tenant pooling).</summary>
public sealed class FhirConnectionPoolOptions
{
    /// <summary>Max concurrent connections to a single source endpoint. 0 = framework default.</summary>
    public int MaxConnectionsPerServer { get; set; }

    /// <summary>How long a pooled connection is reused before being recycled (helps DNS failover). Default 5 min.</summary>
    public int PooledConnectionLifetimeSeconds { get; set; } = 300;
}

public sealed class MutualTlsOptions
{
    /// <summary>When false (default), no client certificate is attached and calls use server-only TLS.</summary>
    public bool Enabled { get; set; }

    /// <summary>Client certificate as a base64-encoded PKCS#12 (PFX). In production this is sourced from Key Vault.</summary>
    public string? PfxBase64 { get; set; }

    /// <summary>Alternative to <see cref="PfxBase64"/>: a path to a PFX file on disk.</summary>
    public string? PfxFilePath { get; set; }

    /// <summary>Password protecting the PFX, if any.</summary>
    public string? Password { get; set; }
}

/// <summary>
/// Supplies the client certificate used for mutual TLS (mTLS) on outbound FHIR connections and configures an
/// <see cref="HttpClientHandler"/> to present it. Disabled by default; enable via <c>Connectivity:Mtls</c>. The
/// certificate may be provided inline as a base64 PFX (typically injected from Key Vault) or from a file path.
/// </summary>
public sealed class MutualTlsCertificateProvider
{
    private readonly MutualTlsOptions _options;
    private readonly FhirConnectionPoolOptions _poolOptions;
    private readonly ILogger<MutualTlsCertificateProvider> _logger;

    public MutualTlsCertificateProvider(
        IOptions<MutualTlsOptions>? options = null,
        IOptions<FhirConnectionPoolOptions>? poolOptions = null,
        ILogger<MutualTlsCertificateProvider>? logger = null)
    {
        _options = options?.Value ?? new MutualTlsOptions();
        _poolOptions = poolOptions?.Value ?? new FhirConnectionPoolOptions();
        _logger = logger ?? NullLogger<MutualTlsCertificateProvider>.Instance;
    }

    public bool IsEnabled => _options.Enabled;

    /// <summary>Loads the configured client certificate, or null when mTLS is disabled / no source is configured.</summary>
    public X509Certificate2? GetClientCertificate()
    {
        if (!_options.Enabled)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_options.PfxBase64))
        {
            var bytes = Convert.FromBase64String(_options.PfxBase64);
            return X509CertificateLoader.LoadPkcs12(bytes, _options.Password);
        }

        if (!string.IsNullOrWhiteSpace(_options.PfxFilePath) && File.Exists(_options.PfxFilePath))
        {
            var bytes = File.ReadAllBytes(_options.PfxFilePath);
            return X509CertificateLoader.LoadPkcs12(bytes, _options.Password);
        }

        _logger.LogWarning("mTLS is enabled but no client certificate source (PfxBase64/PfxFilePath) is configured.");
        return null;
    }

    /// <summary>Attaches the client certificate to the handler so outbound calls present it during the TLS handshake.</summary>
    public void Configure(HttpClientHandler handler)
    {
        var certificate = GetClientCertificate();
        if (certificate is null)
        {
            return;
        }

        handler.ClientCertificateOptions = ClientCertificateOption.Manual;
        handler.ClientCertificates.Add(certificate);
        _logger.LogInformation("mTLS enabled: attached client certificate {Thumbprint}.", certificate.Thumbprint);
    }

    /// <summary>
    /// Builds a pooled <see cref="SocketsHttpHandler"/> for an HttpClient — applying connection-pool limits
    /// (per-tenant pooling) and attaching the mTLS client certificate when enabled.
    /// </summary>
    public HttpMessageHandler CreatePrimaryHandler(bool enableAutomaticDecompression = false)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(Math.Max(1, _poolOptions.PooledConnectionLifetimeSeconds))
        };

        if (_poolOptions.MaxConnectionsPerServer > 0)
        {
            handler.MaxConnectionsPerServer = _poolOptions.MaxConnectionsPerServer;
        }

        // Transparent gzip: advertise Accept-Encoding: gzip and decompress the response before it reaches the caller.
        // Enabled only for callers that opt in (bulk export, whose NDJSON files compress well). The decompressed stream
        // is handed to the reader exactly as before, so no downstream parsing change is needed. Off by default so no
        // other FHIR client's behavior changes.
        if (enableAutomaticDecompression)
        {
            handler.AutomaticDecompression = DecompressionMethods.GZip;
        }

        var certificate = GetClientCertificate();
        if (certificate is not null)
        {
            handler.SslOptions.ClientCertificates = [certificate];
            _logger.LogInformation("mTLS enabled: attached client certificate {Thumbprint}.", certificate.Thumbprint);
        }

        return handler;
    }
}
