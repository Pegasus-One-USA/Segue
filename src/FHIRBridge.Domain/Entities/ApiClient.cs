using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// A third-party application's OAuth 2.0 Client Credentials Grant (RFC 6749 §4.4) credentials — tenant-wide,
/// not scoped to any single workflow: a valid token minted for this client can trigger any workflow's
/// <c>POST /workflows/{id}/run</c>. <see cref="ClientId"/> is a public identifier (safe to display/copy);
/// only <see cref="ClientSecretHash"/> and the plaintext secret returned once at creation/regeneration time
/// are sensitive.
/// </summary>
public sealed class ApiClient : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private readonly List<ApiClientReturnUrl> _returnUrls = new();

    private ApiClient()
    {
    }

    public ApiClient(string name, string clientId, string clientSecretHash)
    {
        Id = Guid.NewGuid();
        Name = name;
        ClientId = clientId;
        ClientSecretHash = clientSecretHash;
        IsEnabled = true;
    }

    public string Name { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Name;

    /// <summary>Public identifier, e.g. "cid_xxxxxxxxxxxxxxxxxxxxxxxx" — unique, not secret.</summary>
    public string ClientId { get; private set; } = default!;

    /// <summary>Hash of the client's secret (via <see cref="Application.Abstractions.Security.IPasswordHasher"/>)
    /// — the plaintext is never stored, only ever returned once from the create/regenerate response.</summary>
    public string ClientSecretHash { get; private set; } = default!;

    public bool IsEnabled { get; private set; }

    /// <summary>Stamped on every successful token issuance — lets an admin tell a still-active integration
    /// apart from a stale one before deleting it.</summary>
    public DateTime? LastUsedOnUtc { get; private set; }

    public void Rename(string name)
    {
        Name = name;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    /// <summary>Rotates the secret — keeps <see cref="ClientId"/> stable so the client's stored id doesn't
    /// change, only what proves possession of it.</summary>
    public void RegenerateSecret(string newSecretHash)
    {
        ClientSecretHash = newSecretHash;
    }

    public void RecordUsed(DateTime utcNow)
    {
        LastUsedOnUtc = utcNow;
    }

    public IReadOnlyCollection<ApiClientReturnUrl> ReturnUrls => _returnUrls.AsReadOnly();

    public ApiClientReturnUrl AddReturnUrl(string url, string? label, ReturnUrlMatchMode matchMode = ReturnUrlMatchMode.Exact)
    {
        var returnUrl = new ApiClientReturnUrl(Id, url, label, matchMode);
        _returnUrls.Add(returnUrl);
        return returnUrl;
    }

    public bool RemoveReturnUrl(Guid returnUrlId)
    {
        var existing = _returnUrls.FirstOrDefault(x => x.Id == returnUrlId);
        return existing is not null && _returnUrls.Remove(existing);
    }
}
