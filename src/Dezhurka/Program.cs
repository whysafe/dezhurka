using System.Text;
using Dezhurka;

Console.OutputEncoding = Encoding.UTF8;
return Cli.Run(args, Console.Out, Console.Error);

/// <summary>Командная строка: plan, check, example.</summary>
public static class Cli
{
    private const string Usage = """
        dezhurka — честный график дежурств

        Использование:
          dezhurka plan team.json [--format table|csv|ics|md] [-o файл] [--person Имя] [--seed N]
          dezhurka check team.json
          dezhurka example > team.json

        Коды выхода: 0 — график составлен, 1 — есть дни без нужного числа людей, 2 — ошибка в файле или аргументах.
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            return args[0] switch
            {
                "plan" => PlanCommand(args[1..], stdout, stderr),
                "check" => CheckCommand(args[1..], stdout),
                "example" => Write(stdout, Example),
                "--version" or "version" => Write(stdout, "dezhurka 1.0.0"),
                _ => Fail(stderr, $"Неизвестная команда «{args[0]}»\n\n{Usage}"),
            };
        }
        catch (ConfigException e)
        {
            return Fail(stderr, "Ошибка в конфигурации: " + e.Message);
        }
        catch (ArgumentException e)
        {
            return Fail(stderr, e.Message);
        }
    }

    private static int PlanCommand(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? file = null, output = null, person = null;
        var format = "table";
        var seed = 1;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"После {args[i]} нужно значение");
            switch (args[i])
            {
                case "--format" or "-f": format = Next(); break;
                case "--output" or "-o": output = Next(); break;
                case "--person": person = Next(); break;
                case "--seed": seed = int.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed: нужно число"); break;
                default:
                    if (args[i].StartsWith('-') || file is not null)
                    {
                        throw new ArgumentException($"Непонятный аргумент «{args[i]}»");
                    }
                    file = args[i];
                    break;
            }
        }
        if (file is null)
        {
            throw new ArgumentException("Укажите файл с командой: dezhurka plan team.json");
        }
        var cfg = ConfigLoader.Load(file);
        if (person is not null && cfg.People.All(p => p.Name != person))
        {
            throw new ArgumentException($"--person: «{person}» нет в {file}");
        }
        var plan = new Scheduler(cfg, seed).Build();
        var title = "График дежурств";
        var text = format switch
        {
            "table" => Export.Table(plan, cfg),
            "csv" => Export.Csv(plan, cfg),
            "ics" => Export.Ics(plan, person is null ? title : $"{title}: {person}", person),
            "md" or "markdown" => Export.Markdown(plan, cfg, title),
            _ => throw new ArgumentException($"--format: «{format}» не поддерживается, есть table, csv, ics, md"),
        };
        if (output is null)
        {
            stdout.Write(text);
        }
        else
        {
            File.WriteAllText(output, text, new UTF8Encoding(format == "csv"));
            stdout.WriteLine($"Записано: {output} ({plan.Shifts.Count} дней, разброс нагрузки {Text.Num(plan.Spread)})");
        }
        foreach (var g in plan.Gaps)
        {
            stderr.WriteLine($"Внимание: на {Text.Day(g.Date)} не хватает {g.Missing} чел.");
        }
        return plan.Gaps.Count > 0 ? 1 : 0;
    }

    private static int CheckCommand(string[] args, TextWriter stdout)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException("Использование: dezhurka check team.json");
        }
        var cfg = ConfigLoader.Load(args[0]);
        var days = cfg.Days().Count();
        stdout.WriteLine($"{args[0]}: ok. {cfg.People.Count} чел., {days} дней, нужно смен: {days * cfg.PerDay}, " +
                         $"в среднем {Text.Num((double)days * cfg.PerDay / cfg.People.Count)} на человека");
        return 0;
    }

    private static int Write(TextWriter w, string text)
    {
        w.WriteLine(text);
        return 0;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        return 2;
    }

    public const string Example = """
        {
          // Период включительно
          "start": "2026-11-01",
          "end": "2026-11-30",
          // Сколько человек дежурит в день
          "perDay": 1,
          // Минимум дней отдыха между сменами одного человека
          "minRestDays": 1,
          // Максимум смен в неделю (можно переопределить у человека)
          "maxPerWeek": 3,
          // Выходные и праздники тяжелее будней
          "weekendWeight": 1.5,
          "holidayWeight": 2,
          "holidays": ["2026-11-04"],
          "people": [
            { "name": "Анна", "unavailable": ["2026-11-09..2026-11-15"] },
            { "name": "Борис", "never": ["сб", "вс"] },
            { "name": "Вера", "maxPerWeek": 2 },
            { "name": "Глеб", "unavailable": ["2026-11-20"] },
            { "name": "Дина" },
            { "name": "Егор", "never": ["пн"] }
          ]
        }
        """;
}
