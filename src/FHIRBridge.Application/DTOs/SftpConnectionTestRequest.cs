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
    string? RemoteFolder,
    // When re-testing an already-saved destination without retyping its password, Password is blank and this
    // carries the destination's id so the test service can resolve the stored one via ISecretProvider instead.
    Guid? DestinationId = null);

/// <summary>Result of a non-relational (e.g. SFTP) connection test: whether it connected, and any error.</summary>
public sealed record ConnectionTestResultDto(bool Connected, string? Error);
