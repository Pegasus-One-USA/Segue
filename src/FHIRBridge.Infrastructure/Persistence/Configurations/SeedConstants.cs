namespace FHIRBridge.Infrastructure.Persistence.Configurations;

internal static class SeedConstants
{
    /// <summary>
    /// Fixed creation timestamp for seeded (HasData) rows. Must be a compile-time constant — EF migrations
    /// require deterministic seed values, so <c>DateTime.UtcNow</c> cannot be used here.
    /// </summary>
    public static readonly DateTime SeedTimestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
