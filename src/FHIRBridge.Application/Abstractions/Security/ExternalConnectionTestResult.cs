namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>Result of a connection test; <see cref="Message"/> is safe to show to an admin (never a secret or token).</summary>
public sealed record ExternalConnectionTestResult(bool Success, string Message);
