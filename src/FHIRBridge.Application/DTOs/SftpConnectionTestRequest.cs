namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Ad-hoc SFTP connection details for a not-yet-saved CSV destination (storageType 'sftp'), used by the
/// destination wizard's Test Connection button. No secret is persisted.
/// </summary>
public sealed record SftpConnectionTestRequest(
    string Host,
    int Port,
    string Username,
    string? Password,
    string? RemoteFolder);

/// <summary>Result of a non-relational (e.g. SFTP) connection test: whether it connected, and any error.</summary>
public sealed record ConnectionTestResultDto(bool Connected, string? Error);
