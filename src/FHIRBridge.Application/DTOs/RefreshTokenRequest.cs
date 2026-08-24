namespace FHIRBridge.Application.DTOs;

// Nullable/optional: the refresh endpoint's real source of truth is the HttpOnly cookie (see
// AuthController.Refresh), and the client legitimately posts an empty "{}" body since it never
// holds the raw token. A non-nullable RefreshToken here would fail ASP.NET Core's model
// validation on that empty body before the controller's own cookie-fallback logic ever runs.
public sealed record RefreshTokenRequest(string? RefreshToken = null);
