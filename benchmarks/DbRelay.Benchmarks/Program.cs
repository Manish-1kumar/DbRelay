using BenchmarkDotNet.Running;
using DbRelay.Benchmarks;

// Usage:
//   dotnet run -c Release --project benchmarks/DbRelay.Benchmarks -- enqueue [BenchmarkDotNet args]
//   dotnet run -c Release --project benchmarks/DbRelay.Benchmarks -- throughput <PostgreSql|SqlServer> [messages] [output.md]
// Requires DBRELAY_TEST_POSTGRES / DBRELAY_TEST_SQLSERVER.

var mode = args.Length > 0 ? args[0] : "help";
switch (mode)
{
    case "enqueue":
        BenchmarkSwitcher.FromTypes([typeof(EnqueueBenchmarks)]).Run(args[1..]);
        break;

    case "throughput":
        ClaimCounter.EnsureStarted();
        var provider = Enum.Parse<Provider>(args.Length > 1 ? args[1] : "PostgreSql");
        var messages = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5000;
        var table = await ThroughputHarness.RunAsync(
            provider,
            messages,
            batchSizes: [10, 100, 500],
            concurrencies: [1, 8, 32],
            workers: [1, 4]);
        if (args.Length > 3)
        {
            await File.WriteAllTextAsync(args[3], table);
        }

        break;

    default:
        Console.WriteLine("Modes: enqueue | throughput <PostgreSql|SqlServer> [messages] [output.md]");
        break;
}
