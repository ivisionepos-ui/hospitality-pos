namespace Pos.Domain.Common;

/// <summary>
/// A repeating weekly window, e.g. "Tuesday and Wednesday, all day" or "Friday 22:00 to 02:00".
/// Used by scheduled offers and, later, by time-based menus and deal availability.
/// </summary>
public sealed class WeeklyTimeWindow
{
    private readonly HashSet<DayOfWeek> _days;

    /// <summary>
    /// Creates a window. Leave both times empty for all day.
    /// If the end time is earlier than (or equal to) the start time, the window runs past midnight,
    /// and the listed day is the day the window starts.
    /// </summary>
    public WeeklyTimeWindow(IEnumerable<DayOfWeek> days, TimeOnly? startTime = null, TimeOnly? endTime = null)
    {
        ArgumentNullException.ThrowIfNull(days);
        _days = [.. days];
        if (_days.Count == 0)
        {
            throw new ArgumentException("At least one day is required.", nameof(days));
        }

        if (startTime.HasValue != endTime.HasValue)
        {
            throw new ArgumentException("Give both a start and an end time, or neither for all day.");
        }

        StartTime = startTime;
        EndTime = endTime;
    }

    /// <summary>Days the window starts on.</summary>
    public IReadOnlySet<DayOfWeek> Days => _days;

    /// <summary>Start time (inclusive), or null for all day.</summary>
    public TimeOnly? StartTime { get; }

    /// <summary>End time (exclusive), or null for all day.</summary>
    public TimeOnly? EndTime { get; }

    /// <summary>True when the given local date and time falls inside the window.</summary>
    public bool Contains(DateTime localTime)
    {
        var day = localTime.DayOfWeek;
        if (StartTime is not { } start || EndTime is not { } end)
        {
            return _days.Contains(day);
        }

        var time = TimeOnly.FromDateTime(localTime);
        if (start < end)
        {
            return _days.Contains(day) && time >= start && time < end;
        }

        // Runs past midnight: the evening part belongs to today, the early-morning part to yesterday.
        var previousDay = (DayOfWeek)(((int)day + 6) % 7);
        return (_days.Contains(day) && time >= start) || (_days.Contains(previousDay) && time < end);
    }
}
