using Pos.Domain.Common;

namespace Pos.Domain.Tests.Common;

public class WeeklyTimeWindowTests
{
    // 3 Nov 2026 is a Tuesday.
    private static DateTime Tue(int hour, int minute = 0) => new(2026, 11, 3, hour, minute, 0);

    private static DateTime Wed(int hour, int minute = 0) => new(2026, 11, 4, hour, minute, 0);

    private static DateTime Thu(int hour, int minute = 0) => new(2026, 11, 5, hour, minute, 0);

    [Fact]
    public void All_day_window_matches_listed_days_only()
    {
        var window = new WeeklyTimeWindow([DayOfWeek.Tuesday, DayOfWeek.Wednesday]);

        Assert.True(window.Contains(Tue(0, 0)));
        Assert.True(window.Contains(Wed(23, 59)));
        Assert.False(window.Contains(Thu(12)));
    }

    [Fact]
    public void Time_window_includes_start_and_excludes_end()
    {
        var window = new WeeklyTimeWindow([DayOfWeek.Tuesday], new TimeOnly(15, 0), new TimeOnly(18, 0));

        Assert.False(window.Contains(Tue(14, 59)));
        Assert.True(window.Contains(Tue(15, 0)));
        Assert.True(window.Contains(Tue(17, 59)));
        Assert.False(window.Contains(Tue(18, 0)));
        Assert.False(window.Contains(Wed(16)));
    }

    [Fact]
    public void Window_past_midnight_belongs_to_the_day_it_starts()
    {
        // "Tuesday 22:00 to 02:00" covers Tuesday night and the early hours of Wednesday.
        var window = new WeeklyTimeWindow([DayOfWeek.Tuesday], new TimeOnly(22, 0), new TimeOnly(2, 0));

        Assert.True(window.Contains(Tue(23)));
        Assert.True(window.Contains(Wed(1, 30)));
        Assert.False(window.Contains(Wed(2, 0)));
        Assert.False(window.Contains(Tue(1, 0)));   // early Tuesday belongs to Monday night
        Assert.False(window.Contains(Wed(23)));     // Wednesday night is not listed
    }

    [Fact]
    public void Requires_at_least_one_day()
    {
        Assert.Throws<ArgumentException>(() => new WeeklyTimeWindow([]));
    }

    [Fact]
    public void Requires_both_times_or_neither()
    {
        Assert.Throws<ArgumentException>(() => new WeeklyTimeWindow([DayOfWeek.Monday], new TimeOnly(9, 0)));
    }
}
