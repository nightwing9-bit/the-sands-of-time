using System.Diagnostics;
using System.Text.Json;
using Integration.Tests.Support;

namespace Integration.Tests.GhostLap;

/// <summary>Launches the built Realtime entry point against the isolated integration database.</summary>
internal sealed class BenchmarkRealtimeProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<string> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamWriter _log;

    public BenchmarkRealtimeProcess(IdentityDatabase db, string secret, int matches, int connections, int spectatorsPerRoom, string profile, int? emptyRoomSeconds = null)
    {
        var hostDll = Environment.GetEnvironmentVariable("GHOSTLAP_LOAD_HOST_DLL");
        if (!File.Exists(hostDll))
        {
            throw new FileNotFoundException("Build Hosts.Realtime through the benchmark script.", hostDll);
        }

        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(hostDll)!,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add(hostDll!);
        info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        info.Environment["ConnectionStrings__Postgres"] = db.RealtimeConnectionString;
        info.Environment["ConnectionStrings__Redis"] = "localhost:6399,abortConnect=false";
        info.Environment["Identity__MasterSecret"] = secret;
        info.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        const string prefix = "GhostLap__Multiplayer__";
        info.Environment[prefix + "CountdownMilliseconds"] = "1000";
        info.Environment[prefix + "MeasureSnapshotBytes"] = "true";
        info.Environment[prefix + "ProfileOutputPath"] = profile;
        info.Environment[prefix + "ProfileIntervalSeconds"] = "1";
        info.Environment[prefix + "MaxConcurrentMatches"] = matches.ToString();
        info.Environment[prefix + "MaxTotalConnections"] = Math.Max(6, connections).ToString();
        info.Environment[prefix + "MaxSpectators"] = Math.Max(8, spectatorsPerRoom).ToString();
        info.Environment[prefix + "CodeLookupsPerMinute"] = "600";

        if (emptyRoomSeconds is { } seconds)
        {
            info.Environment[prefix + "EmptyRoomSeconds"] = seconds.ToString();
        }

        _log = new StreamWriter(Path.Combine(Path.GetDirectoryName(profile)!, "host.log")) { AutoFlush = true };
        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Log(e.Data);
        _process.ErrorDataReceived += (_, e) => Log(e.Data);
        _process.Exited += (_, _) => _listening.TrySetException(new InvalidOperationException("Realtime host exited. See host.log."));
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int Id => _process.Id;

    public Task<string> WaitForAddressAsync() => _listening.Task.WaitAsync(TimeSpan.FromSeconds(20));

    public async Task KillAsync()
    {
        _process.Kill();
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void Log(string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (_log)
        {
            _log.WriteLine(line);
        }
        if (line.Contains("Now listening on: ", StringComparison.Ordinal))
        {
            using var entry = JsonDocument.Parse(line);
            var address = entry.RootElement.GetProperty("State").GetProperty("address").GetString()!;
            _listening.TrySetResult(address);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                using var signal = Process.Start("kill", $"-TERM {_process.Id}")!;
                await signal.WaitForExitAsync();
                try
                {
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            _process.WaitForExit(); // drain the stdout/stderr callbacks
        }
        finally
        {
            _process.Dispose();
            await _log.DisposeAsync();
        }
    }
}
