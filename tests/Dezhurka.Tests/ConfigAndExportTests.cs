using Dezhurka;
using Xunit;

namespace Dezhurka.Tests;

public class ConfigTests
{
    [Fact]
    public void Example_config_is_valid()
    {
        var cfg = ConfigLoader.Parse(Cli.Example);
        Assert.Equal(6, cfg.People.Count);
        Assert.Equal(30, cfg.Days().Count());
        Assert.Equal(7, cfg.People[0].Unavailable.Count);
        Assert.Contains(DayOfWeek.Saturday, cfg.People[1].NeverOn);
    }

    [Theory]
    [InlineData("""{"start":"2026-11-01","end":"2026-10-01","people":[{"name":"А"}]}""", "end раньше start")]
    [InlineData("""{"start":"01.11.2026","end":"2026-11-30","people":[{"name":"А"}]}""", "ГГГГ-ММ-ДД")]
    [InlineData("""{"start":"2026-11-01","end":"2026-11-30","people":[{"name":"А"},{"name":"а"}]}""", "встречается дважды")]
    [InlineData("""{"start":"2026-11-01","end":"2026-11-30","people":[{"name":"А","never":["пятница"]}]}""", "неизвестный день недели")]
    [InlineData("""{"start":"2026-11-01","end":"2026-11-30","perDay":3,"people":[{"name":"А"},{"name":"Б"}]}""", "меньше, чем нужно")]
    [InlineData("""{"start":"2026-11-01","end":"2026-11-30","people":[{"name":"А","unavailable":["2026-11-09..2026-11-01"]}]}""", "конец раньше начала")]
    [InlineData("""{"start":"2026-11-01","end":"2026-11-30","people":[{"name":""}]}""", "нет имени")]
    [InlineData("""{"start":"2026-11-01", "end": }""", "Ошибка в JSON")]
    public void Broken_configs_explain_the_problem(string json, string expected)
    {
        var e = Assert.Throws<ConfigException>(() => ConfigLoader.Parse(json));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Ranges_are_inclusive()
    {
        var days = ConfigLoader.DatesOf("2026-12-30..2027-01-02", "x").ToList();
        Assert.Equal(4, days.Count);
        Assert.Equal(new DateOnly(2027, 1, 2), days[^1]);
    }
}

public class ExportTests
{
    private static (Plan, TeamConfig) Sample()
    {
        var cfg = ConfigLoader.Parse(Cli.Example);
        return (new Scheduler(cfg).Build(), cfg);
    }

    [Fact]
    public void Ics_is_a_valid_calendar_with_crlf_and_all_day_events()
    {
        var (plan, _) = Sample();
        var ics = Export.Ics(plan, "График");
        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));
        Assert.Equal(30, ics.Split("BEGIN:VEVENT").Length - 1);
        Assert.Contains("DTSTART;VALUE=DATE:20261101\r\nDTEND;VALUE=DATE:20261102", ics);
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
    }

    [Fact]
    public void Personal_calendar_contains_only_that_persons_shifts()
    {
        var (plan, _) = Sample();
        var count = plan.Shifts.Count(s => s.People.Contains("Дина"));
        var ics = Export.Ics(plan, "График", "Дина");
        Assert.Equal(count, ics.Split("BEGIN:VEVENT").Length - 1);
    }

    [Fact]
    public void Ics_uids_are_stable_between_runs()
    {
        var (plan, _) = Sample();
        Assert.Equal(Export.Ics(plan, "x"), Export.Ics(plan, "x"));
    }

    [Fact]
    public void Ics_escapes_and_folds_long_lines()
    {
        Assert.Equal("a\\, b\\; c\\\\d\\nе", Export.Escape("a, b; c\\d\nе"));
        var folded = Export.Fold("SUMMARY:" + new string('ж', 60));
        Assert.Contains("\r\n ", folded);
        Assert.All(folded.Split("\r\n"), l => Assert.True(System.Text.Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.Equal("SUMMARY:" + new string('ж', 60), folded.Replace("\r\n ", ""));
    }

    [Fact]
    public void Csv_has_a_row_per_day_and_quotes_when_needed()
    {
        var (plan, cfg) = Sample();
        var lines = Export.Csv(plan, cfg).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("date,weekday,type,people", lines[0]);
        Assert.Equal(31, lines.Length);
        Assert.Contains(lines, l => l.StartsWith("2026-11-04,ср,holiday,"));
        Assert.Equal("\"a, b\"", Export.CsvField("a, b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Export.CsvField("say \"hi\""));
    }

    [Fact]
    public void Markdown_and_table_show_every_person()
    {
        var (plan, cfg) = Sample();
        var md = Export.Markdown(plan, cfg, "График");
        var table = Export.Table(plan, cfg);
        foreach (var p in cfg.People)
        {
            Assert.Contains($"| {p.Name} |", md);
            Assert.Contains(p.Name, table);
        }
        Assert.Contains("Разброс нагрузки", table);
    }
}

public class CliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (Cli.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void Example_then_plan_then_check()
    {
        var path = Path.Combine(Path.GetTempPath(), $"team-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, Run("example").Out);
        try
        {
            Assert.Equal(0, Run("check", path).Code);
            var plan = Run("plan", path, "--format", "md");
            Assert.Equal(0, plan.Code);
            Assert.Contains("| Кто | Смен |", plan.Out);
            var ics = Path.ChangeExtension(path, ".ics");
            Assert.Equal(0, Run("plan", path, "-f", "ics", "-o", ics, "--person", "Анна").Code);
            Assert.Contains("BEGIN:VCALENDAR", File.ReadAllText(ics));
            File.Delete(ics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Errors_give_exit_code_2()
    {
        Assert.Equal(2, Run().Code);
        Assert.Equal(2, Run("frobnicate").Code);
        Assert.Equal(2, Run("plan").Code);
        Assert.Equal(2, Run("plan", "missing.json").Code);
        var (code, _, err) = Run("plan", "missing.json");
        Assert.Contains("dezhurka example", err);
        Assert.Equal(2, code);
    }
}
