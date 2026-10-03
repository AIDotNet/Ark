using System.Globalization;

namespace Ark.Sync;

/// <summary>
/// 最小 cron 解析（5 字段：分 时 日 月 周；支持 * , - /）。
/// day-of-week: 0/7=周日。日与周同时受限时按标准 cron 语义取"或"。仅支持服务器本地时区。
/// </summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];      // 1..31
    private readonly bool[] _months = new bool[13];    // 1..12
    private readonly bool[] _weekdays = new bool[8];   // 0..7（7 归一为 0）
    private readonly bool _anyDay;
    private readonly bool _anyWeekday;

    private CronExpression(bool anyDay, bool anyWeekday)
    {
        _anyDay = anyDay;
        _anyWeekday = anyWeekday;
    }

    public static CronExpression Parse(string expression)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            throw new FormatException($"cron 需要恰好 5 个字段（分 时 日 月 周）: \"{expression}\"");
        var cron = new CronExpression(parts[2] == "*", parts[4] == "*");
        ParseField(parts[0], 0, 59, v => cron._minutes[v] = true);
        ParseField(parts[1], 0, 23, v => cron._hours[v] = true);
        ParseField(parts[2], 1, 31, v => cron._days[v] = true);
        ParseField(parts[3], 1, 12, v => cron._months[v] = true);
        ParseField(parts[4], 0, 7, v => cron._weekdays[v % 7] = true);
        return cron;
    }

    private static void ParseField(string field, int min, int max, Action<int> set)
    {
        foreach (var token in field.Split(','))
        {
            var (range, step) = SplitStep(token);
            var (lo, hi) = ParseRange(range, min, max);
            for (var v = lo; v <= hi; v += step) set(Normalize(v, min, max));
        }
    }

    private static (string, int) SplitStep(string token)
    {
        var idx = token.IndexOf('/');
        if (idx < 0) return (token, 1);
        var step = int.Parse(token[(idx + 1)..], CultureInfo.InvariantCulture);
        if (step <= 0) throw new FormatException($"cron 步长必须 > 0: {token}");
        return (token[..idx], step);
    }

    private static (int, int) ParseRange(string range, int min, int max)
    {
        if (range == "*") return (min, max);
        var idx = range.IndexOf('-');
        if (idx < 0)
        {
            var v = int.Parse(range, CultureInfo.InvariantCulture);
            return (v, v);
        }
        var lo = int.Parse(range[..idx], CultureInfo.InvariantCulture);
        var hi = int.Parse(range[(idx + 1)..], CultureInfo.InvariantCulture);
        return (lo, hi);
    }

    private static int Normalize(int v, int min, int max) => Math.Clamp(v, min, max);

    /// <summary>after 之后的下一次触发时间（不含 after 本身所在分钟）。最多向后搜索 366 天。</summary>
    public DateTimeOffset NextOccurrence(DateTimeOffset after)
    {
        var t = new DateTimeOffset(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Offset)
            .AddMinutes(1);
        var limit = t.AddDays(366);
        while (t <= limit)
        {
            if (!_months[t.Month]) { t = NextMonth(t); continue; }
            var dayOk = _days[t.Day];
            var weekOk = _weekdays[(int)t.DayOfWeek];
            var dayRestricted = !_anyDay;
            var weekRestricted = !_anyWeekday;
            var match = (dayRestricted, weekRestricted) switch
            {
                (true, true) => dayOk || weekOk,   // 标准 cron：两者都受限取或
                (true, false) => dayOk,
                (false, true) => weekOk,
                _ => true,
            };
            if (!match) { t = NextDay(t); continue; }
            if (!_hours[t.Hour]) { t = NextHour(t); continue; }
            if (!_minutes[t.Minute]) { t = t.AddMinutes(1); continue; }
            return t;
        }
        throw new FormatException("cron 表达式在 366 天内无触发时间");
    }

    private static DateTimeOffset NextMonth(DateTimeOffset t) =>
        t.Month == 12 ? new DateTimeOffset(t.Year + 1, 1, 1, 0, 0, 0, t.Offset) : new DateTimeOffset(t.Year, t.Month + 1, 1, 0, 0, 0, t.Offset);

    private static DateTimeOffset NextDay(DateTimeOffset t) =>
        new DateTimeOffset(t.Year, t.Month, t.Day, 0, 0, 0, t.Offset).AddDays(1);

    private static DateTimeOffset NextHour(DateTimeOffset t) =>
        new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset).AddHours(1);
}
