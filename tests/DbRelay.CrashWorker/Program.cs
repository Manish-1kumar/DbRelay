using System.Diagnostics;
using System.Globalization;
using System.Text;
using DbRelay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage:
//   dotnet DbRelay.CrashWorker.dll --connection <cs> --schema <schema> --marker <file> --mode hang|kill
//       [--max-attempts N] [--lease-ms N] [--timeout-ms N] [--poll-ms N]
//
// mode=hang: the handler appends "DELIVERED <id> <attempt>", flushes to disk, then blocks forever (ignoring cancellation).
//            The parent test kills the process tree, simulating a worker that dies right after the external system got the message.
// mode=kill: the handler appends the marker, flushes, then terminates its own process (TerminateProcess / SIGKILL).
var settings = ParseArguments(args);
var connectionString = Required(settings, "connection");
var schema = Required(settings, "schema");
var marker = Required(settings, "marker");
var mode = Required(settings, "mode");
if (mode is not ("hang" or "kill"))
{
    Console.Error.WriteLine($"Unknown mode '{mode}'.");
    return 2;
}

var maxAttempts = Number(settings, "max-attempts", 10);
var lease = TimeSpan.FromMilliseconds(Number(settings, "lease-ms", 4000));
var timeout = TimeSpan.FromMilliseconds(Number(settings, "timeout-ms", 2000));
var poll = TimeSpan.FromMilliseconds(Number(settings, "poll-ms", 100));

AppendLine(marker, $"STARTED {Environment.ProcessId}");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Services.AddDbRelay(relay =>
{
    relay.UsePostgreSql(connectionString, pg => pg.Schema = schema);
    relay.UseDeliveryHandler(async (message, _) =>
    {
        AppendLine(marker, $"DELIVERED {message.Id} {message.Attempt}");
        if (mode == "kill")
        {
            // A hard kill: no graceful shutdown, no lease release, no outcome recorded.
            Process.GetCurrentProcess().Kill();
        }

        // Block forever, deliberately ignoring the delivery-timeout token, until the parent kills this process.
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
        return DeliveryResult.Delivered();
    });
    relay.ConfigureDispatcher(d =>
    {
        d.BatchSize = 1;
        d.MaxConcurrency = 1;
        d.PollingInterval = poll;
        d.LeaseDuration = lease;
        d.DeliveryTimeout = timeout;
        d.MaxErrorBackoff = TimeSpan.FromSeconds(1);
    });
    relay.ConfigureRetry(r => r.MaxAttempts = maxAttempts);
    relay.ConfigureMaintenance(m =>
    {
        m.CleanupEnabled = false;
        m.StatisticsInterval = null;
    });
});

using var host = builder.Build();
await host.RunAsync();
return 0;

static void AppendLine(string path, string line)
{
    var bytes = Encoding.UTF8.GetBytes(line + "\n");
    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    stream.Write(bytes, 0, bytes.Length);
    stream.Flush(flushToDisk: true);
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected an option name at position {i}.");
        }

        result[args[i][2..]] = args[i + 1];
    }

    return result;
}

static string Required(Dictionary<string, string> settings, string name) =>
    settings.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing --{name}.");

static int Number(Dictionary<string, string> settings, string name, int fallback) =>
    settings.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
