using BoundlessStudy.Application.Abstractions;

namespace BoundlessStudy.Desktop.Services;

/// <summary>
/// Default desktop local clock implementation using the local system clock.
/// Avoids any dependency on BoundlessStudy.Infrastructure.
/// </summary>
public sealed class SystemClock : ILocalClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public int UtcOffsetMinutes => (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;

    public DateOnly ToLocalDate(DateTime utcTime)
    {
        var local = utcTime.Kind == DateTimeKind.Utc ? utcTime.ToLocalTime() : utcTime;
        return DateOnly.FromDateTime(local);
    }
}
