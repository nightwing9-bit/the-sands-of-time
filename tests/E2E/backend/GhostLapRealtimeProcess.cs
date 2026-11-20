using System.Diagnostics;

namespace Sands.E2E.Backend;

/// <summary>Optional browser integration fixture: unmodified production Realtime entry point, isolated DB and shared guest secret.</summary>
internal sealed class GhostLapRealtimeProcess(Process process) : IDisposable
{
    public static GhostLapRealtimeProcess? Start(E2eDatabase db, string secret)
    {
        var portText = Environment.GetEnvironmentVariable("E2E_GHOSTLAP_REALTIME_PORT");
        if (portText is null) return null;
        if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535) throw new InvalidOperationException("Invalid Realtime port");
        var dll = Path.Combine(E2eDatabase.RepositoryRoot(), "src", "Hosts.Realtime", "bin", "Release", "net10.0", "Hosts.Realtime.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("Build Hosts.Realtime in Release first", dll);
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(dll)!, UseShellExecute = false };
        info.ArgumentList.Add(dll);
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        info.Environment["ConnectionStrings__Postgres"] = db.GhostLapRealtimeConnectionString;
        info.Environment["ConnectionStrings__Redis"] = "localhost:6399,abortConnect=false";
        info.Environment["ConnectionStrings__RevenantLedger"] = "";
        info.Environment["ConnectionStrings__GhostLapSolo"] = "";
        info.Environment["Identity__MasterSecret"] = secret;
        // Browser sends the Vite origin through its WebSocket proxy; never relax the hub's origin gate.
        info.Environment["GhostLap__Multiplayer__AllowedOrigins__0"] = "http://localhost:5174";
        return new GhostLapRealtimeProcess(Process.Start(info)!);
    }
    public void Dispose()
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        process.Dispose();
    }
}
