using System.Text.Json;

namespace AStockWidget.Services;

/// <summary>
/// Determines whether A-share market is open.
/// Uses bundled holiday / make-up-workday calendar first, then weekdays.
/// </summary>
public sealed class MarketCalendar
{
    private static readonly TimeZoneInfo ChinaTimeZone =
        FindChinaTimeZone();

    private readonly Dictionary<string, bool> _offDays; // date -> isOffDay

    public MarketCalendar()
    {
        _offDays = LoadFromDisk();
    }

    private static TimeZoneInfo FindChinaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")
                ?? TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    public static DateTime GetChinaNow()
    {
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ChinaTimeZone);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    public bool IsMarketOpen() => IsMarketOpen(GetChinaNow());

    private static Dictionary<string, bool> LoadFromDisk()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "market_days.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Fall back to weekday rule.
        }

        return new Dictionary<string, bool>();
    }

    public bool IsTradingDay(DateTime date)
    {
        var key = date.ToString("yyyy-MM-dd");
        if (_offDays.TryGetValue(key, out var isOffDay))
        {
            return !isOffDay;
        }

        // Default rule: Monday to Friday are trading days.
        return date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
    }

    public bool IsMarketOpen(DateTime now)
    {
        if (!IsTradingDay(now.Date))
        {
            return false;
        }

        var time = now.TimeOfDay;
        var morningStart = new TimeSpan(9, 30, 0);
        var morningEnd = new TimeSpan(11, 30, 0);
        var afternoonStart = new TimeSpan(13, 0, 0);
        var afternoonEnd = new TimeSpan(15, 0, 0);

        return (time >= morningStart && time <= morningEnd)
            || (time >= afternoonStart && time <= afternoonEnd);
    }
}