using System.Diagnostics;

namespace DbRelay.Soak;

/// <summary>A worker child process, started from this same executable in "worker" mode.</summary>
public sealed class WorkerProcess
{
    private readonly Process _process;
    private readonly StreamWriter _log;

    private WorkerProcess(string name, bool immune, string stopFile, Process process, StreamWriter log)
    {
        Name = name;
        Immune = immune;
        StopFile = stopFile;
        _process = process;
        _log = log;
    }

    public string Name { get; }

    /// <summary>Never killed by chaos; it is the long-lived memory/handle probe.</summary>
    public bool Immune { get; }

    public string StopFile { get; }

    public bool HasExited => _process.HasExited;

    public static WorkerProcess Start(Provider provider, string schema, string name, string workDir, bool immune)
    {
        var stopFile = Path.Combine(workDir, $"stop-{name}-{Guid.NewGuid():N}");
        var self = typeof(WorkerProcess).Assembly.Location;
        var host = Environment.ProcessPath!;
        var runningUnderDotnet = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(runningUnderDotnet ? host : Path.ChangeExtension(self, OperatingSystem.IsWindows() ? ".exe" : null))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (runningUnderDotnet)
        {
            info.ArgumentList.Add(self);
        }

        foreach (var arg in new[] { "worker", provider.ToString(), schema, name, stopFile })
        {
            info.ArgumentList.Add(arg);
        }

        var log = new StreamWriter(Path.Combine(workDir, $"{name}.log"), append: true) { AutoFlush = true };
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start a worker process.");
        process.OutputDataReceived += (_, e) => Write(log, e.Data);
        process.ErrorDataReceived += (_, e) => Write(log, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new WorkerProcess(name, immune, stopFile, process, log);
    }

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }

    public async Task StopGracefullyAsync(TimeSpan timeout)
    {
        if (_process.HasExited)
        {
            return;
        }

        await File.WriteAllTextAsync(StopFile, "stop");
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(); // did not stop in time; record it in the log and kill
            Write(_log, "coordinator: graceful stop timed out; killed");
        }
    }

    public (long Rss, int Threads, int Handles) Measure()
    {
        try
        {
            _process.Refresh();
            return _process.HasExited ? (0, 0, 0) : (_process.WorkingSet64, _process.Threads.Count, _process.HandleCount);
        }
        catch (InvalidOperationException)
        {
            return (0, 0, 0);
        }
    }

    private static void Write(StreamWriter log, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (log)
        {
            log.WriteLine(line);
        }
    }
}
