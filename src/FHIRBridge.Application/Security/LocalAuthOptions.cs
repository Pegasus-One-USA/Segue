namespace FHIRBridge.Application.Security;

public sealed class LocalAuthOptions
{
    /// <summary>URL template for password-reset emails; supports {token} and {email} placeholders.</summary>
    public string? PasswordResetUrlTemplate { get; set; }

    /// <summary>URL template for invitation emails; supports {token} and {email} placeholders.</summary>
    public string? AcceptInviteUrlTemplate { get; set; }
}
