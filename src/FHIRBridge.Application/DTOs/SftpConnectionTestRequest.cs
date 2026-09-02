namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc SFTP connection details for a not-yet-saved CSV destination (storageType 'sftp'), used by the
/// destination wizard's Test Connection button. No secret is persisted.
/// </summary>
/// <param name="DestinationId">When set and <paramref name="Password"/> is blank, the password is resolved
/// server-side from this already-saved destination's stored secret instead of the (blank) request field — lets
/// the wizard verify an existing connection without the browser ever holding or resending the real password.
/// Ignored when <paramref name="Password"/> is non-blank (a genuinely new/changed password always wins).</param>
public sealed record SftpConnectionTestRequest(
    string Host,
    int Port,
    string Username,
    string? Password,
    string? RemoteFolder,
    Guid? DestinationId = null);

/// <summary>Result of a non-relational (e.g. SFTP) connection test: whether it connected, and any error.</summary>
public sealed record ConnectionTestResultDto(bool Connected, string? Error);
