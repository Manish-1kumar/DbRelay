using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DbRelay.IntegrationTests;

/// <summary>Runs <c>tests/DbRelay.CrashWorker</c> as a real child process with <c>dotnet &lt;dll&gt;</c>.</summary>
public sealed class ChildWorker : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private ChildWorker(Process process)
    {
        _process = process;
    }

    public int Pid { get; private set; }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    public static string WorkerDll
    {
        get
        {
            var path = typeof(ChildWorker).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "CrashWorkerPath").Value!;
            Assert.True(File.Exists(path), $"The crash worker was not built: {path}");
            return path;
        }
    }

    public static ChildWorker Start(
        string connectionString,
        string schema,
        string markerPath,
        string mode,
        int maxAttempts,
        TimeSpan lease,
        TimeSpan deliveryTimeout)
    {
        var info = new ProcessStartInfo(DotnetHost())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(WorkerDll);
        foreach (var (name, value) in new[]
        {
            ("--connection", connectionString),
            ("--schema", schema),
            ("--marker", markerPath),
            ("--mode", mode),
            ("--max-attempts", maxAttempts.ToString(CultureInfo.InvariantCulture)),
            ("--lease-ms", ((int)lease.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
            ("--timeout-ms", ((int)deliveryTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
            ("--poll-ms", "100"),
        })
        {
            info.ArgumentList.Add(name);
            info.ArgumentList.Add(value);
        }

        var process = new Process { StartInfo = info };
        var worker = new ChildWorker(process);
        process.OutputDataReceived += (_, e) => worker.Append(e.Data);
        process.ErrorDataReceived += (_, e) => worker.Append(e.Data);
        Assert.True(process.Start(), "The crash worker process did not start.");
        worker.Pid = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return worker;
    }

    /// <summary>Kills the whole process tree (no graceful shutdown) and waits until the OS reports it gone.</summary>
    public void KillTree()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        Assert.True(_process.WaitForExit(30_000), "The killed worker did not exit within 30s.");
    }

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process with this id
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(30_000);
            }
        }
        catch (InvalidOperationException)
        {
            // never started or already gone
        }

        _process.Dispose();
    }

    public static IReadOnlyList<string> ReadMarker(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public static IReadOnlyList<string> ReadDeliveries(string path) =>
        ReadMarker(path).Where(l => l.StartsWith("DELIVERED ", StringComparison.Ordinal)).ToList();

    private static string DotnetHost()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.ProcessPath is { } current && string.Equals(Path.GetFileName(current), exe, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
        {
            return hostPath;
        }

        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        return "dotnet";
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_output)
        {
            _output.AppendLine(line);
        }
    }
}
