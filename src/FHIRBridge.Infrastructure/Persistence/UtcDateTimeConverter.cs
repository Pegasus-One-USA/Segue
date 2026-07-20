using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// SQL Server's datetime2 columns carry no timezone info, so EF Core always materializes
/// DateTime values as Kind=Unspecified. Without Kind=Utc, System.Text.Json omits the "Z"
/// suffix and browsers parse the timestamp as local time instead of converting it, so the
/// portal ends up displaying raw UTC digits as if they were already the viewer's local time.
/// This stamps every DateTime as Utc on both write and read so serialization is correct.
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}

public sealed class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public NullableUtcDateTimeConverter()
        : base(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
    {
    }
}
