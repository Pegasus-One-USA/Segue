namespace FHIRBridge.Application.DTOs;

public sealed record AppSecretDto(
    string SecretName,
    string DisplayName,
    bool Provisioned,
    DateTime? LastRotatedUtc,
    bool RestartRequiredForFullEffect);
