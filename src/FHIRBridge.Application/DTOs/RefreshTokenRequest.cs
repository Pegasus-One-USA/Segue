namespace FHIRBridge.Application.DTOs;

// Nullable: the refresh token normally arrives via the fhirbridge_refresh_token HttpOnly cookie (see
// AuthController.Refresh), not this body — the Angular client posts an empty {} object. A non-nullable
// string here would make [ApiController]'s implicit "required" validation reject that empty body with a
// 400 before the controller's own cookie-fallback logic ever runs.
public sealed record RefreshTokenRequest(string? RefreshToken);
