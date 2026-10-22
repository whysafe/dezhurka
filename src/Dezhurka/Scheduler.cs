namespace Dezhurka;

/// <summary>Кто дежурит в конкретный день.</summary>
public sealed record Shift(DateOnly Date, IReadOnlyList<string> People);

/// <summary>День, который не удалось закрыть, и почему каждый человек не подошёл.</summary>
public sealed record Gap(DateOnly Date, int Missing, IReadOnlyDictionary<string, string> Reasons);

public sealed record PersonStats(string Name, int Shifts, int Weekends, int Holidays, double Load);

public sealed record Plan(IReadOnlyList<Shift> Shifts, IReadOnlyList<Gap> Gaps, IReadOnlyList<PersonStats> Stats)
{
    /// <summary>Разница нагрузки между самым и наименее загруженным: чем меньше, тем справедливее.</summary>
    public double Spread => Stats.Count == 0 ? 0 : Stats.Max(s => s.Load) - Stats.Min(s => s.Load);
}

/// <summary>
/// Составляет график в два этапа. Сначала жадно: каждый день берём доступных людей
/// с наименьшей взвешенной нагрузкой. Потом улучшаем перестановками: передаём смену от
/// самого загруженного к менее загруженному, пока это уменьшает разброс и не нарушает правил.
/// </summary>
public sealed class Scheduler(TeamConfig config, int seed = 1)
{
    private readonly TeamConfig cfg = config;
    private readonly Random random = new(seed);

    public Plan Build()
    {
        var days = cfg.Days().ToList();
        var assigned = days.ToDictionary(d => d, _ => new List<string>());
        var gaps = new List<Gap>();
        // Порядок людей перемешиваем по зерну: при равной нагрузке выбор не зависит от порядка в файле.
        var order = cfg.People.Select(p => p.Name).OrderBy(_ => random.Next()).ToList();
        var rank = order.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);

        foreach (var day in days)
        {
            var load = Loads(assigned);
            var candidates = cfg.People
                .Where(p => Reason(p, day, assigned) is null)
                .OrderBy(p => load[p.Name] + cfg.Weight(day))
                .ThenBy(p => CountWhere(assigned, p.Name, d => cfg.Weight(d) > 1) * (cfg.Weight(day) > 1 ? 1 : 0))
                .ThenBy(p => rank[p.Name])
                .Take(cfg.PerDay)
                .ToList();
            assigned[day].AddRange(candidates.Select(p => p.Name));
            if (candidates.Count < cfg.PerDay)
            {
                var reasons = cfg.People
                    .Where(p => !assigned[day].Contains(p.Name))
                    .ToDictionary(p => p.Name, p => Reason(p, day, assigned) ?? "уже в графике");
                gaps.Add(new Gap(day, cfg.PerDay - candidates.Count, reasons));
            }
        }

        Improve(assigned, days);

        var shifts = days.Select(d => new Shift(d, assigned[d].OrderBy(n => n, StringComparer.Ordinal).ToList())).ToList();
        return new Plan(shifts, gaps, Stats(assigned));
    }

    /// <summary>Почему человек не может дежурить в этот день; null — может.</summary>
    internal string? Reason(Person p, DateOnly day, Dictionary<DateOnly, List<string>> assigned)
    {
        if (assigned[day].Contains(p.Name))
        {
            return "уже в этот день";
        }
        if (p.Unavailable.Contains(day))
        {
            return "недоступен";
        }
        if (p.NeverOn.Contains(day.DayOfWeek))
        {
            return "не дежурит по " + Text.DayShort[(int)day.DayOfWeek];
        }
        for (var k = 1; k <= cfg.MinRestDays; k++)
        {
            foreach (var near in new[] { day.AddDays(-k), day.AddDays(k) })
            {
                if (assigned.TryGetValue(near, out var list) && list.Contains(p.Name))
                {
                    return $"отдыхает после смены {near:dd.MM}";
                }
            }
        }
        var weekStart = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        var inWeek = Enumerable.Range(0, 7)
            .Select(weekStart.AddDays)
            .Count(d => assigned.TryGetValue(d, out var list) && list.Contains(p.Name));
        var cap = p.MaxPerWeek ?? cfg.MaxPerWeek;
        if (inWeek >= cap)
        {
            return $"уже {inWeek} смен(ы) на этой неделе";
        }
        return null;
    }

    private Dictionary<string, double> Loads(Dictionary<DateOnly, List<string>> assigned)
    {
        var load = cfg.People.ToDictionary(p => p.Name, _ => 0.0);
        foreach (var (day, names) in assigned)
        {
            foreach (var n in names)
            {
                load[n] += cfg.Weight(day);
            }
        }
        return load;
    }

    private static int CountWhere(Dictionary<DateOnly, List<string>> assigned, string name, Func<DateOnly, bool> pred) =>
        assigned.Count(kv => kv.Value.Contains(name) && pred(kv.Key));

    /// <summary>
    /// Локальный поиск. Ход «передать»: смена переходит от одного человека к другому.
    /// Ход «обменять»: двое меняются сменами, например выходной на будний. Принимается первый ход,
    /// который уменьшает штраф, и так до тех пор, пока улучшать нечего.
    /// </summary>
    private void Improve(Dictionary<DateOnly, List<string>> assigned, List<DateOnly> days)
    {
        var people = cfg.People.ToDictionary(p => p.Name);
        var names = cfg.People.Select(p => p.Name).ToList();
        for (var round = 0; round < 2000; round++)
        {
            var before = Penalty(assigned);
            if (!TryTransfer(assigned, days, people, names, before) && !TrySwap(assigned, days, people, names, before))
            {
                return;
            }
        }
    }

    /// <summary>Штраф несправедливости: разброс общей нагрузки плюс разброс числа тяжёлых смен (выходные и праздники).</summary>
    private double Penalty(Dictionary<DateOnly, List<string>> assigned)
    {
        var load = Loads(assigned);
        var heavy = cfg.People.ToDictionary(p => p.Name, _ => 0.0);
        foreach (var (day, list) in assigned)
        {
            if (cfg.Weight(day) > 1)
            {
                foreach (var n in list)
                {
                    heavy[n]++;
                }
            }
        }
        return Variance(load.Values) + Variance(heavy.Values);
    }

    private bool TryTransfer(Dictionary<DateOnly, List<string>> assigned, List<DateOnly> days,
                             Dictionary<string, Person> people, List<string> names, double before)
    {
        foreach (var day in days)
        {
            foreach (var giver in assigned[day].ToList())
            {
                foreach (var taker in names.Where(n => n != giver))
                {
                    assigned[day].Remove(giver);
                    if (Reason(people[taker], day, assigned) is null)
                    {
                        assigned[day].Add(taker);
                        if (Penalty(assigned) < before - 1e-9)
                        {
                            return true;
                        }
                        assigned[day].Remove(taker);
                    }
                    assigned[day].Add(giver);
                }
            }
        }
        return false;
    }

    private bool TrySwap(Dictionary<DateOnly, List<string>> assigned, List<DateOnly> days,
                         Dictionary<string, Person> people, List<string> names, double before)
    {
        foreach (var d1 in days)
        {
            foreach (var d2 in days.Where(d => d > d1 && cfg.Weight(d) != cfg.Weight(d1)))
            {
                foreach (var a in assigned[d1].ToList())
                {
                    foreach (var b in assigned[d2].ToList())
                    {
                        if (a == b || assigned[d1].Contains(b) || assigned[d2].Contains(a))
                        {
                            continue;
                        }
                        assigned[d1].Remove(a);
                        assigned[d2].Remove(b);
                        if (Reason(people[b], d1, assigned) is null)
                        {
                            assigned[d1].Add(b);
                            if (Reason(people[a], d2, assigned) is null)
                            {
                                assigned[d2].Add(a);
                                if (Penalty(assigned) < before - 1e-9)
                                {
                                    return true;
                                }
                                assigned[d2].Remove(a);
                            }
                            assigned[d1].Remove(b);
                        }
                        assigned[d1].Add(a);
                        assigned[d2].Add(b);
                    }
                }
            }
        }
        return false;
    }

    private static double Variance(IEnumerable<double> values)
    {
        var list = values.ToList();
        var mean = list.Average();
        return list.Sum(v => (v - mean) * (v - mean));
    }

    private List<PersonStats> Stats(Dictionary<DateOnly, List<string>> assigned) =>
        cfg.People.Select(p =>
        {
            var days = assigned.Where(kv => kv.Value.Contains(p.Name)).Select(kv => kv.Key).ToList();
            return new PersonStats(
                p.Name,
                days.Count,
                days.Count(TeamConfig.IsWeekend),
                days.Count(cfg.IsHoliday),
                Math.Round(days.Sum(cfg.Weight), 2));
        }).ToList();
}
