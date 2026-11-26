using System.Globalization;
using DbRelay.Soak;

// coordinator --provider <PostgreSql|SqlServer|Oracle> [--minutes 120] [--workers 3] [--rate 40] [--report soak.md] [--keep]
// worker <provider> <schema> <name> <stopFile>        (started by the coordinator)
if (args.Length > 0 && args[0] == "worker")
{
    return await Worker.RunAsync(Enum.Parse<Provider>(args[1]), args[2], args[3], args[4]);
}

if (args.Length > 0 && args[0] == "coordinator")
{
    string Arg(string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    var provider = Enum.Parse<Provider>(Arg("--provider", "PostgreSql"));
    var minutes = double.Parse(Arg("--minutes", "120"), CultureInfo.InvariantCulture);
    var workers = int.Parse(Arg("--workers", "3"), CultureInfo.InvariantCulture);
    var rate = double.Parse(Arg("--rate", "40"), CultureInfo.InvariantCulture);
    var report = Arg("--report", $"soak-{provider}.md");
    return await Coordinator.RunAsync(provider, TimeSpan.FromMinutes(minutes), workers, report, rate, keep: args.Contains("--keep"));
}

Console.WriteLine("Usage: coordinator --provider PostgreSql|SqlServer|Oracle [--minutes 120] [--workers 3] [--rate 40] [--report soak.md] [--keep]");
return 2;
