using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dezhurka;

public static class Text
{
    // Индекс совпадает с DayOfWeek: 0 — воскресенье.
    public static readonly string[] DayShort = ["вс", "пн", "вт", "ср", "чт", "пт", "сб"];

    public static string Day(DateOnly d) => $"{d:dd.MM} {DayShort[(int)d.DayOfWeek]}";

    public static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Форматы вывода графика.</summary>
public static class Export
{
    /// <summary>Таблица для консоли: дата, кто дежурит, отметки выходных и праздников, затем итоги.</summary>
    public static string Table(Plan plan, TeamConfig cfg)
    {
        var sb = new StringBuilder();
        var width = Math.Max(10, plan.Shifts.Max(s => string.Join(", ", s.People).Length));
        foreach (var s in plan.Shifts)
        {
            var mark = cfg.IsHoliday(s.Date) ? " праздник" : TeamConfig.IsWeekend(s.Date) ? " выходной" : "";
            var who = s.People.Count == 0 ? "— никого —" : string.Join(", ", s.People);
            sb.AppendLine($"{Text.Day(s.Date)}  {who.PadRight(width)}{mark}");
        }
        sb.AppendLine();
        sb.Append(Stats(plan));
        foreach (var g in plan.Gaps)
        {
            sb.AppendLine();
            sb.AppendLine($"Не хватает {g.Missing} чел. на {Text.Day(g.Date)}:");
            foreach (var (name, why) in g.Reasons)
            {
                sb.AppendLine($"  {name}: {why}");
            }
        }
        return sb.ToString();
    }

    public static string Stats(Plan plan)
    {
        var sb = new StringBuilder();
        var nameWidth = Math.Max(6, plan.Stats.Max(s => s.Name.Length));
        sb.AppendLine($"{"Кто".PadRight(nameWidth)}  смен  выходных  праздников  нагрузка");
        foreach (var s in plan.Stats.OrderByDescending(s => s.Load).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            sb.AppendLine($"{s.Name.PadRight(nameWidth)}  {s.Shifts,4}  {s.Weekends,8}  {s.Holidays,10}  {Text.Num(s.Load),8}");
        }
        sb.AppendLine($"Разброс нагрузки: {Text.Num(plan.Spread)}");
        return sb.ToString();
    }

    /// <summary>CSV для Excel и Google Таблиц: по строке на день, люди через «; ».</summary>
    public static string Csv(Plan plan, TeamConfig cfg)
    {
        var sb = new StringBuilder("date,weekday,type,people\r\n");
        foreach (var s in plan.Shifts)
        {
            var type = cfg.IsHoliday(s.Date) ? "holiday" : TeamConfig.IsWeekend(s.Date) ? "weekend" : "workday";
            sb.Append($"{s.Date:yyyy-MM-dd},{Text.DayShort[(int)s.Date.DayOfWeek]},{type},{CsvField(string.Join("; ", s.People))}\r\n");
        }
        return sb.ToString();
    }

    internal static string CsvField(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>
    /// Календарь iCalendar (RFC 5545): событие на весь день для каждой смены. Импортируется в
    /// Google Календарь, Outlook и Apple Календарь. Если указан person, в файле только его смены.
    /// </summary>
    public static string Ics(Plan plan, string calendarName, string? person = null)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(Fold(text)).Append("\r\n");
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//whysafe//dezhurka 1.0//RU");
        Line("CALSCALE:GREGORIAN");
        Line("X-WR-CALNAME:" + Escape(calendarName));
        foreach (var s in plan.Shifts)
        {
            if (s.People.Count == 0 || (person is not null && !s.People.Contains(person)))
            {
                continue;
            }
            var who = string.Join(", ", s.People);
            Line("BEGIN:VEVENT");
            // UID стабилен: при повторном импорте того же графика календарь обновит событие, а не создаст копию.
            Line($"UID:{Uid(calendarName, s.Date, person)}@dezhurka");
            Line("DTSTAMP:" + s.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "T000000Z");
            Line("DTSTART;VALUE=DATE:" + s.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line("DTEND;VALUE=DATE:" + s.Date.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line("SUMMARY:" + Escape(person is null ? $"Дежурство: {who}" : "Дежурство"));
            Line("DESCRIPTION:" + Escape($"Дежурят: {who}"));
            Line("TRANSP:TRANSPARENT");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    internal static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

    /// <summary>Строки длиннее 75 байт переносятся с пробелом в начале продолжения (RFC 5545, 3.1).</summary>
    internal static string Fold(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75)
        {
            return line;
        }
        var sb = new StringBuilder();
        var count = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (count + size > limit)
            {
                sb.Append("\r\n ");
                count = 0;
                limit = 74; // у продолжения первый байт занят пробелом
            }
            sb.Append(rune.ToString());
            count += size;
        }
        return sb.ToString();
    }

    private static string Uid(string calendar, DateOnly date, string? person)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{calendar}|{date:yyyy-MM-dd}|{person}"));
        return Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }

    /// <summary>Markdown: таблица графика и итоги, удобно вставить в вики или задачу.</summary>
    public static string Markdown(Plan plan, TeamConfig cfg, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"{cfg.Start:dd.MM.yyyy} — {cfg.End:dd.MM.yyyy}, по {cfg.PerDay} чел. в день");
        sb.AppendLine();
        sb.AppendLine("| Дата | Кто | |");
        sb.AppendLine("|---|---|---|");
        foreach (var s in plan.Shifts)
        {
            var mark = cfg.IsHoliday(s.Date) ? "праздник" : TeamConfig.IsWeekend(s.Date) ? "выходной" : "";
            var who = s.People.Count == 0 ? "**никого**" : string.Join(", ", s.People);
            sb.AppendLine($"| {Text.Day(s.Date)} | {who} | {mark} |");
        }
        sb.AppendLine();
        sb.AppendLine("| Кто | Смен | Выходных | Праздников | Нагрузка |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var s in plan.Stats.OrderByDescending(s => s.Load).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {s.Name} | {s.Shifts} | {s.Weekends} | {s.Holidays} | {Text.Num(s.Load)} |");
        }
        return sb.ToString();
    }
}
