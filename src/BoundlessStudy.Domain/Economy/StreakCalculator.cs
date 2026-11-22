namespace BoundlessStudy.Domain.Economy;

/// <summary>
/// Streak rules (ECONOMY-1 §8.8). Pure functions over the set of qualified local
/// dates derived from completed sessions' recorded completion local dates.
/// Timezone changes never rewrite historical days; breaks never affect the streak.
/// </summary>
public static class StreakCalculator
{
    /// <summary>
    /// Current streak as of <paramref name="asOfDate"/>. The streak counts backward
    /// from the as-of date or the day immediately before it (an unqualified today
    /// does not erase yesterday's streak); otherwise it is 0.
    /// </summary>
    public static int CurrentStreak(IReadOnlyCollection<DateOnly> qualifiedLocalDates, DateOnly asOfDate)
    {
        var days = new HashSet<DateOnly>(qualifiedLocalDates);
        days.RemoveWhere(d => d > asOfDate); // future-dated records do not extend a streak

        if (days.Count == 0)
        {
            return 0;
        }

        if (!days.Contains(asOfDate) && !days.Contains(asOfDate.AddDays(-1)))
        {
            return 0;
        }

        var cursor = days.Contains(asOfDate) ? asOfDate : asOfDate.AddDays(-1);
        var streak = 0;
        while (days.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return streak;
    }

    /// <summary>Longest consecutive run of qualified days.</summary>
    public static int LongestStreak(IReadOnlyCollection<DateOnly> qualifiedLocalDates)
    {
        if (qualifiedLocalDates.Count == 0)
        {
            return 0;
        }

        var sorted = qualifiedLocalDates.Distinct().OrderBy(d => d).ToArray();
        var longest = 1;
        var run = 1;
        for (var i = 1; i < sorted.Length; i++)
        {
            run = sorted[i].DayNumber == sorted[i - 1].DayNumber + 1 ? run + 1 : 1;
            if (run > longest)
            {
                longest = run;
            }
        }

        return longest;
    }

    /// <summary>The local date on which the current streak next increments, or null when the streak is broken pending qualification.</summary>
    public static DateOnly? NextQualifyingDay(IReadOnlyCollection<DateOnly> qualifiedLocalDates, DateOnly asOfDate)
    {
        var current = CurrentStreak(qualifiedLocalDates, asOfDate);
        if (current == 0)
        {
            // Either today or yesterday was the last qualified day (streak alive),
            // or no streak exists yet: the next qualification becomes 1 either way.
            return asOfDate;
        }

        if (qualifiedLocalDates.Contains(asOfDate))
        {
            return asOfDate.AddDays(1);
        }

        // Streak is alive through yesterday; qualifying today keeps it alive.
        return asOfDate;
    }
}
