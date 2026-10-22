using Dezhurka;
using Xunit;

namespace Dezhurka.Tests;

public class SchedulerTests
{
    private static TeamConfig Config(string people, string extra = "") => ConfigLoader.Parse($$"""
        {
          "start": "2026-11-01", "end": "2026-11-30", "perDay": 1, "minRestDays": 1, "maxPerWeek": 7,
          "holidays": ["2026-11-04"] {{extra}},
          "people": [{{people}}]
        }
        """);

    private const string Six = """
        {"name":"Анна"},{"name":"Борис"},{"name":"Вера"},{"name":"Глеб"},{"name":"Дина"},{"name":"Егор"}
        """;

    private static IEnumerable<(DateOnly Day, string Name)> Assignments(Plan plan) =>
        plan.Shifts.SelectMany(s => s.People.Select(p => (s.Date, p)));

    [Fact]
    public void Every_day_gets_the_required_number_of_distinct_people()
    {
        var cfg = Config(Six, ", \"perDay\": 2");
        var plan = new Scheduler(cfg).Build();
        Assert.Equal(30, plan.Shifts.Count);
        Assert.Empty(plan.Gaps);
        Assert.All(plan.Shifts, s =>
        {
            Assert.Equal(2, s.People.Count);
            Assert.Equal(2, s.People.Distinct().Count());
        });
    }

    [Fact]
    public void Unavailable_dates_and_never_days_are_respected()
    {
        var cfg = Config("""
            {"name":"Анна","unavailable":["2026-11-09..2026-11-15"]},
            {"name":"Борис","never":["сб","вс"]},
            {"name":"Вера"},{"name":"Глеб"}
            """);
        var plan = new Scheduler(cfg).Build();
        foreach (var (day, name) in Assignments(plan))
        {
            if (name == "Анна")
            {
                Assert.False(day >= new DateOnly(2026, 11, 9) && day <= new DateOnly(2026, 11, 15), $"Анна в {day}");
            }
            if (name == "Борис")
            {
                Assert.False(TeamConfig.IsWeekend(day), $"Борис в выходной {day}");
            }
        }
    }

    [Fact]
    public void Nobody_works_two_days_in_a_row_with_one_rest_day()
    {
        var plan = new Scheduler(Config(Six)).Build();
        var byPerson = Assignments(plan).GroupBy(a => a.Name);
        foreach (var g in byPerson)
        {
            var days = g.Select(a => a.Day).Order().ToList();
            for (var k = 1; k < days.Count; k++)
            {
                Assert.True(days[k].DayNumber - days[k - 1].DayNumber >= 2, $"{g.Key}: {days[k - 1]} и {days[k]}");
            }
        }
    }

    [Fact]
    public void Weekly_cap_is_respected()
    {
        var cfg = Config("""{"name":"Анна","maxPerWeek":1},{"name":"Борис"},{"name":"Вера"},{"name":"Глеб"}""", ", \"minRestDays\": 0");
        var plan = new Scheduler(cfg).Build();
        var weeks = Assignments(plan).Where(a => a.Name == "Анна")
            .GroupBy(a => a.Day.AddDays(-(((int)a.Day.DayOfWeek + 6) % 7)));
        Assert.All(weeks, w => Assert.Single(w));
    }

    [Fact]
    public void Load_is_shared_fairly_between_equal_people()
    {
        var plan = new Scheduler(Config(Six)).Build();
        // Самая тяжёлая смена весит 2 (праздник): справедливый график не расходится больше чем на одну такую смену.
        Assert.True(plan.Spread <= 2, $"разброс {plan.Spread}");
        var shifts = plan.Stats.Select(s => s.Shifts).ToList();
        Assert.True(shifts.Max() - shifts.Min() <= 2);
    }

    [Fact]
    public void Weekends_are_spread_across_people()
    {
        var plan = new Scheduler(Config(Six)).Build();
        var weekends = plan.Stats.Select(s => s.Weekends).ToList();
        Assert.Equal(9, weekends.Sum()); // в ноябре 2026 девять выходных дней
        Assert.True(weekends.Max() - weekends.Min() <= 1, string.Join(",", weekends));
    }

    [Fact]
    public void Impossible_day_is_reported_with_a_reason_for_everyone()
    {
        // 10 ноября 2026 — вторник: Анна недоступна, Борис по вторникам не дежурит.
        var cfg = Config("""
            {"name":"Анна","unavailable":["2026-11-10"]},
            {"name":"Борис","never":["вт"]}
            """) with { End = new DateOnly(2026, 11, 12), MinRestDays = 0 }; // без дня отдыха конфликт только 10-го
        var plan = new Scheduler(cfg).Build();
        var gap = Assert.Single(plan.Gaps);
        Assert.Equal(new DateOnly(2026, 11, 10), gap.Date);
        Assert.Equal("недоступен", gap.Reasons["Анна"]);
        Assert.Equal("не дежурит по вт", gap.Reasons["Борис"]);
    }

    [Fact]
    public void Same_seed_gives_the_same_plan_and_different_seeds_may_differ()
    {
        var cfg = Config(Six);
        var a = Export.Csv(new Scheduler(cfg, 7).Build(), cfg);
        var b = Export.Csv(new Scheduler(cfg, 7).Build(), cfg);
        Assert.Equal(a, b);
        var seeds = Enumerable.Range(1, 10).Select(s => Export.Csv(new Scheduler(cfg, s).Build(), cfg)).Distinct();
        Assert.True(seeds.Count() > 1);
    }

    [Fact]
    public void Weekends_stay_fair_when_someone_never_works_weekends()
    {
        // Пример из README: Борис не дежурит по выходным, у Анны отпуск, у Веры лимит две смены в неделю.
        var cfg = ConfigLoader.Parse(Cli.Example);
        var plan = new Scheduler(cfg).Build();
        var weekends = plan.Stats.Where(s => s.Name != "Борис").Select(s => s.Weekends + s.Holidays).ToList();
        Assert.True(weekends.Max() - weekends.Min() <= 1, string.Join(",", weekends));
        Assert.Equal(0, plan.Stats.Single(s => s.Name == "Борис").Weekends);
        Assert.True(plan.Spread <= 1, $"разброс {plan.Spread}");
    }

    [Fact]
    public void Holidays_and_weekends_weigh_more()
    {
        var cfg = Config(Six, ", \"weekendWeight\": 1.5, \"holidayWeight\": 2");
        Assert.Equal(1, cfg.Weight(new DateOnly(2026, 11, 2)));
        Assert.Equal(1.5, cfg.Weight(new DateOnly(2026, 11, 7)));
        Assert.Equal(2, cfg.Weight(new DateOnly(2026, 11, 4)));
    }
}
