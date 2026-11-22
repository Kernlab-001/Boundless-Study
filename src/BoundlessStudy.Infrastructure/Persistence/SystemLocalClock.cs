using BoundlessStudy.Application.Abstractions;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// Real local clock. UTC timestamps are stored with the offset that was in effect
/// at the instant, so later timezone/DST changes do not rewrite history
/// (domain-business-rules.md §10).
/// </summary>
public sealed class SystemLocalClock : ILocalClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public int UtcOffsetMinutes => (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes;

    public DateOnly ToLocalDate(DateTime utcTime)
    {
        var utc = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        var offset = TimeZoneInfo.Local.GetUtcOffset(utc);
        return DateOnly.FromDateTime(utc.Add(offset));
    }
}
