using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Domain.Entities;

public sealed class SourceConnection : AuditableChildEntity<Guid>
{
    private SourceConnection()
    {
    }

    public SourceConnection(
        string name,
        SourceSystemType sourceSystemType,
        string baseUrl,
        SourceAuthenticationConfiguration authentication,
        ApplicationType? applicationType = null,
        SourceInteractiveConfiguration? interactive = null,
        SourceRetrievalConfiguration? retrieval = null)
    {
        Id = Guid.NewGuid();
        Name = name;
        SourceSystemType = sourceSystemType;
        BaseUrl = ValidateBaseUrl(baseUrl);
        Authentication = authentication;
        ApplicationType = applicationType;
        Interactive = interactive;
        Retrieval = retrieval;
        IsEnabled = true;
    }

    public string Name { get; private set; } = default!;
    public SourceSystemType SourceSystemType { get; private set; }
    public string BaseUrl { get; private set; } = default!;
    public SourceAuthenticationConfiguration Authentication { get; private set; } = default!;

    /// <summary>
    /// The SMART application type (composition axis) this source is connected under. Null means legacy behaviour:
    /// the access-token grant is inferred from the vendor and the credentials present.
    /// </summary>
    public ApplicationType? ApplicationType { get; private set; }

    /// <summary>Interactive (authorization-code) settings; null for non-interactive (Backend) sources.</summary>
    public SourceInteractiveConfiguration? Interactive { get; private set; }

    /// <summary>How a Backend System source polls for resources; null for interactive sources and legacy Backend
    /// connections configured before retrieval settings existed.</summary>
    public SourceRetrievalConfiguration? Retrieval { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Update(
        string name,
        SourceSystemType sourceSystemType,
        string baseUrl,
        SourceAuthenticationConfiguration authentication,
        ApplicationType? applicationType = null,
        SourceInteractiveConfiguration? interactive = null,
        SourceRetrievalConfiguration? retrieval = null)
    {
        Name = name;
        SourceSystemType = sourceSystemType;
        BaseUrl = ValidateBaseUrl(baseUrl);
        Authentication = authentication;
        ApplicationType = applicationType;
        Interactive = interactive;
        Retrieval = retrieval;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    /// <summary>Advances the incremental-sync cursor after a workflow run completes successfully. No-op when this
    /// connection has no retrieval configuration (interactive sources, or Backend sources created before this field
    /// existed).</summary>
    public void RecordRetrievalSync(DateTime syncedAtUtc)
    {
        if (Retrieval is not null)
        {
            Retrieval = Retrieval.WithLastSuccessfulSync(syncedAtUtc);
        }
    }

    /// <summary>
    /// Replaces the requested OAuth scopes, leaving every other authentication field untouched. Used by
    /// <c>IEpicSourceConnectionScopeSyncService</c> to keep scopes derived from actual pipeline usage rather than a
    /// manually-curated value that can drift out of sync with what the connection's pipelines really consume.
    /// </summary>
    public void UpdateScopes(string[] scopes)
    {
        Authentication = Authentication.WithScopes(scopes);
    }

    /// <summary>
    /// PHI travels over this connection, so the endpoint must be encrypted in transit (HIPAA
    /// §164.312(e)). HTTPS is required; plain HTTP is permitted only for loopback addresses to
    /// support local development and integration tests.
    /// </summary>
    private static string ValidateBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Source connection BaseUrl is required.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Source connection BaseUrl '{baseUrl}' is not a valid absolute URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return baseUrl;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return baseUrl;
        }

        throw new InvalidOperationException(
            $"Source connection BaseUrl must use HTTPS (got '{uri.Scheme}'). Plain HTTP is allowed only for loopback addresses.");
    }
}
