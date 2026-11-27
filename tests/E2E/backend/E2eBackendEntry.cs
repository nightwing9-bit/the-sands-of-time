using System.Net;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Npgsql;
namespace Sands.E2E.Backend;
/// <summary>
/// Process entry point started by Playwright (<c>tests/E2E/playwright.real.config.ts</c>): starts PostgreSQL, applies the
/// migrations, serves the real <c>Hosts.Api</c> composition plus the test-only Timeline surface on loopback, and stays up until it
/// is signalled. Bounded: the process ends itself after <c>E2E_BACKEND_MAX_SECONDS</c> (default 1800) so a missed teardown cannot
/// leave a container or a server running.
/// Environment: <c>E2E_FIXTURE_TOKEN</c> (required, any random string), <c>E2E_BACKEND_PORT</c> (default 18081).
/// </summary>
public static class E2eBackendEntry
{
    public static async Task<int> Main()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("E2E.Backend is test-only and refuses to run with ASPNETCORE_ENVIRONMENT=Production.");
            return 2;
        }
        var token = Environment.GetEnvironmentVariable("E2E_FIXTURE_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
        {
            Console.Error.WriteLine("E2E_FIXTURE_TOKEN is required (at least 16 characters).");
            return 2;
        }
        var port = int.TryParse(Environment.GetEnvironmentVariable("E2E_BACKEND_PORT"), out var p) ? p : 18081;
        var maxSeconds = int.TryParse(Environment.GetEnvironmentVariable("E2E_BACKEND_MAX_SECONDS"), out var s) ? s : 1800;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(maxSeconds));
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => stop.Cancel());
        Console.WriteLine("E2E_BACKEND_STARTING postgres container");
        await using var database = await E2eDatabase.StartAsync(stop.Token);
        await using var factory = new E2eBackendFactory(database, token);
        factory.UseKestrel(options => options.Listen(Environment.GetEnvironmentVariable("E2E_R8_LISTEN_ANY") == "1" && database.R8Ledger.Length > 0 ? IPAddress.Any : IPAddress.Loopback, port));
        factory.StartServer();
        using var ghostLap = GhostLapRealtimeProcess.Start(database, factory.MasterSecret);
        using var replay = database.R8Ledger.Length > 0 ? await StartR8Async(database, stop.Token) : null;
        Console.WriteLine($"E2E_BACKEND_READY http://127.0.0.1:{port}");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
        if (replay is { HasExited: false }) replay.Kill(entireProcessTree: true);
        Console.WriteLine("E2E_BACKEND_STOPPING");
        return 0;
    }

    private static async Task<Process> StartR8Async(E2eDatabase db, CancellationToken ct)
    {
        await using (var restricted = NpgsqlDataSource.Create(db.R8Replay))
        await using (var forbidden = restricted.CreateCommand("UPDATE revenant.ledger_entry SET amount_minor=amount_minor"))
        {
            try
            {
                await forbidden.ExecuteNonQueryAsync(ct);
                throw new InvalidOperationException("Replay credential has live ledger write authority.");
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }
        }
        var worker = Path.Combine(E2eDatabase.RepositoryRoot(), "src", "Hosts.Worker", "bin", "Debug", "net10.0", "Hosts.Worker.dll");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add("--replay");
        start.Environment["ConnectionStrings__RevenantReplay"] = db.R8Replay;
        start.Environment["Revenant__Ledger__TenantId"] = R8FixtureStartup.Tenant;
        start.Environment.Remove("ConnectionStrings__RevenantLedger");
        return Process.Start(start) ?? throw new InvalidOperationException("Cannot start the Replay worker.");
    }
}
