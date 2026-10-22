using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dezhurka;

/// <summary>Человек в графике. Ограничения необязательны.</summary>
public sealed record Person(
    string Name,
    IReadOnlySet<DateOnly> Unavailable,
    IReadOnlySet<DayOfWeek> NeverOn,
    int? MaxPerWeek);

/// <summary>Всё, что нужно планировщику. Создаётся через <see cref="ConfigLoader"/>.</summary>
public sealed record TeamConfig(
    DateOnly Start,
    DateOnly End,
    int PerDay,
    int MinRestDays,
    int MaxPerWeek,
    IReadOnlySet<DateOnly> Holidays,
    double WeekendWeight,
    double HolidayWeight,
    IReadOnlyList<Person> People)
{
    public IEnumerable<DateOnly> Days()
    {
        for (var d = Start; d <= End; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    public bool IsHoliday(DateOnly d) => Holidays.Contains(d);

    public static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Вес смены: будни 1, выходной и праздник тяжелее (берётся больший вес).</summary>
    public double Weight(DateOnly d) =>
        IsHoliday(d) ? Math.Max(HolidayWeight, IsWeekend(d) ? WeekendWeight : 1)
        : IsWeekend(d) ? WeekendWeight
        : 1;
}

public sealed class ConfigException(string message) : Exception(message);

/// <summary>Читает team.json и проверяет его с понятными сообщениями.</summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Dictionary<string, DayOfWeek> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["пн"] = DayOfWeek.Monday, ["вт"] = DayOfWeek.Tuesday, ["ср"] = DayOfWeek.Wednesday,
        ["чт"] = DayOfWeek.Thursday, ["пт"] = DayOfWeek.Friday, ["сб"] = DayOfWeek.Saturday,
        ["вс"] = DayOfWeek.Sunday,
        ["mon"] = DayOfWeek.Monday, ["tue"] = DayOfWeek.Tuesday, ["wed"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["fri"] = DayOfWeek.Friday, ["sat"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday,
    };

    private sealed class RawConfig
    {
        public string? Start { get; set; }
        public string? End { get; set; }
        public int PerDay { get; set; } = 1;
        public int MinRestDays { get; set; } = 1;
        public int MaxPerWeek { get; set; } = 7;
        public List<string> Holidays { get; set; } = [];
        public double WeekendWeight { get; set; } = 1.5;
        public double HolidayWeight { get; set; } = 2;
        public List<RawPerson> People { get; set; } = [];
    }

    private sealed class RawPerson
    {
        public string? Name { get; set; }
        public List<string> Unavailable { get; set; } = [];
        [JsonPropertyName("never")]
        public List<string> NeverOn { get; set; } = [];
        public int? MaxPerWeek { get; set; }
    }

    public static TeamConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Файл {path} не найден. Пример конфигурации: dezhurka example > team.json");
        }
        return Parse(File.ReadAllText(path));
    }

    public static TeamConfig Parse(string json)
    {
        RawConfig raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawConfig>(json, Json) ?? throw new ConfigException("Пустой файл");
        }
        catch (JsonException e)
        {
            var where = e.LineNumber is { } line ? $" (строка {line + 1})" : "";
            throw new ConfigException($"Ошибка в JSON{where}: {e.Message.Split('.')[0]}");
        }

        var start = Date(raw.Start, "start");
        var end = Date(raw.End, "end");
        if (end < start)
        {
            throw new ConfigException("end раньше start");
        }
        if (end.DayNumber - start.DayNumber > 366)
        {
            throw new ConfigException("Период длиннее года: разбейте его на части");
        }
        if (raw.PerDay < 1)
        {
            throw new ConfigException("perDay должен быть не меньше 1");
        }
        if (raw.MinRestDays < 0 || raw.MaxPerWeek < 1)
        {
            throw new ConfigException("minRestDays не может быть отрицательным, maxPerWeek — меньше 1");
        }
        if (raw.WeekendWeight < 1 || raw.HolidayWeight < 1)
        {
            throw new ConfigException("Веса выходных и праздников не меньше 1");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var people = new List<Person>();
        foreach (var p in raw.People)
        {
            var name = p.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                throw new ConfigException($"У человека №{people.Count + 1} нет имени");
            }
            if (!names.Add(name))
            {
                throw new ConfigException($"Имя «{name}» встречается дважды");
            }
            var unavailable = new HashSet<DateOnly>();
            foreach (var item in p.Unavailable)
            {
                foreach (var d in DatesOf(item, $"{name}: unavailable"))
                {
                    unavailable.Add(d);
                }
            }
            var never = new HashSet<DayOfWeek>();
            foreach (var day in p.NeverOn)
            {
                if (!DayNames.TryGetValue(day.Trim(), out var dow))
                {
                    throw new ConfigException($"{name}: неизвестный день недели «{day}», пишите пн, вт, … вс");
                }
                never.Add(dow);
            }
            if (p.MaxPerWeek is < 1)
            {
                throw new ConfigException($"{name}: maxPerWeek должен быть не меньше 1");
            }
            people.Add(new Person(name, unavailable, never, p.MaxPerWeek));
        }
        if (people.Count < raw.PerDay)
        {
            throw new ConfigException($"Людей ({people.Count}) меньше, чем нужно на один день ({raw.PerDay})");
        }

        var holidays = raw.Holidays.SelectMany(h => DatesOf(h, "holidays")).ToHashSet();
        return new TeamConfig(start, end, raw.PerDay, raw.MinRestDays, raw.MaxPerWeek, holidays,
            raw.WeekendWeight, raw.HolidayWeight, people);
    }

    private static DateOnly Date(string? text, string field)
    {
        if (DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }
        throw new ConfigException($"{field}: нужна дата в формате ГГГГ-ММ-ДД, получено «{text}»");
    }

    /// <summary>«2026-11-03» или диапазон «2026-11-03..2026-11-07» включительно.</summary>
    internal static IEnumerable<DateOnly> DatesOf(string item, string field)
    {
        var parts = item.Split("..", StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return [Date(parts[0], field)];
        }
        if (parts.Length != 2)
        {
            throw new ConfigException($"{field}: «{item}» — ни дата, ни диапазон");
        }
        var (from, to) = (Date(parts[0], field), Date(parts[1], field));
        if (to < from)
        {
            throw new ConfigException($"{field}: в диапазоне «{item}» конец раньше начала");
        }
        return Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays);
    }
}
