namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Set from Program.cs based on the hosting environment (true unless Development) — the Application
/// layer stays decoupled from IHostEnvironment while still enforcing https-only admin-added origins
/// outside local dev.
/// </summary>
public sealed class AllowedCorsOriginsOptions
{
    public bool RequireHttps { get; set; } = true;
}
