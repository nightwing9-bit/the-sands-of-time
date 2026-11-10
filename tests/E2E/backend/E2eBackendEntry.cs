using System.Net;
using System.Runtime.InteropServices;
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
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        factory.StartServer();
        Console.WriteLine($"E2E_BACKEND_READY http://127.0.0.1:{port}");
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
        Console.WriteLine("E2E_BACKEND_STOPPING");
        return 0;
    }
}
