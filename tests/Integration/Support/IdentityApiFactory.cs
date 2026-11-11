using System.Collections.Concurrent;
using System.Security.Cryptography;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Staff;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integration.Tests.Support;

/// <summary>A clock the tests can move to prove expiry without sleeping.</summary>
public sealed class ControllableTime : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }
}

/// <summary>Records every log line (category, id, rendered message, exception text) for hygiene assertions.</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<LogLine> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Lines);

    public void Dispose()
    {
    }

    public string AllText() => string.Join('\n', Lines.Select(l => $"{l.Category} {l.EventId} {l.Message} {l.Exception}"));

    public sealed record LogLine(string Category, int EventId, LogLevel Level, string Message, string? Exception);

    private sealed class CaptureLogger(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new LogLine(category, eventId.Id, logLevel, formatter(state, exception), exception?.ToString()));
    }
}

/// <summary>The real <c>Hosts.Api</c> composition against a real PostgreSQL database.</summary>
public sealed class IdentityApiFactory : WebApplicationFactory<Program>
{
    public static string NewMasterSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private readonly Dictionary<string, string?> _settings;
    private readonly string _environment;

    public IdentityApiFactory(
        IdentityDatabase database,
        string? masterSecret = null,
        string environment = "Testing",
        ControllableTime? time = null,
        Dictionary<string, string?>? extraSettings = null,
        bool withSecret = true)
    {
        _environment = environment;
        Time = time ?? new ControllableTime();
        Logs = new LogCapture();
        MasterSecret = masterSecret ?? NewMasterSecret();
        _settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = database.AppConnectionString,
            ["ConnectionStrings:Redis"] = "localhost:6399,abortConnect=false",
        };
        if (withSecret)
        {
            _settings["Identity:MasterSecret"] = MasterSecret;
        }

        foreach (var (key, value) in extraSettings ?? [])
        {
            _settings[key] = value;
        }
    }

    public ControllableTime Time { get; }
    public LogCapture Logs { get; }
    public string MasterSecret { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<ILoggerProvider>(Logs);
        });
    }

    protected override void ConfigureClient(HttpClient client) => client.BaseAddress = new Uri("https://localhost");

    public TestBrowser NewBrowser(string origin = "https://localhost") =>
        new(CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        }), origin);

    /// <summary>Creates a staff account through the real ASP.NET Core Identity UserManager (the same path the Development seeder uses).</summary>
    public async Task<TestStaff> CreateStaffAsync(string role = IdentityRoles.Operator, string? tenantId = null)
    {
        var name = $"{role}-{Guid.NewGuid():N}@test.sands.local";
        var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var user = new StaffUser { UserName = name };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException("Test account creation failed: " + string.Join(",", created.Errors.Select(e => e.Code)));
        }

        await users.AddToRoleAsync(user, role);
        if (tenantId is not null && !await scope.ServiceProvider.GetRequiredService<StaffAuthService>().GrantTenantAsync(user.Id, tenantId, default))
        {
            throw new InvalidOperationException("Test tenant membership grant failed.");
        }
        return new TestStaff(user.Id, name, password);
    }
}

public sealed record TestStaff(Guid Id, string UserName, string Password);
