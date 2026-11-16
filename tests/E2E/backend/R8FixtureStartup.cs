using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Revenant.Tests.Payments;

namespace Sands.E2E.Backend;

// Prepare the fixture before Kestrel becomes ready. No HTTP route can invoke preparation.
internal sealed class R8FixtureStartup(IServiceProvider services, IHostEnvironment environment) : IHostedService
{
    internal const string Tenant = "r8-browser";

    public async Task StartAsync(CancellationToken ct)
    {
        if (!environment.IsEnvironment("Testing")
            || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Testing")
            throw new InvalidOperationException("The R8 browser fixture requires the Testing environment.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var staff = scope.ServiceProvider.GetRequiredService<StaffAuthService>();
        var password = Environment.GetEnvironmentVariable("E2E_R8_PASSWORD") ?? throw new InvalidOperationException("Missing test password.");
        var identities = new (string Name, string Role)[]
        {
            ("r8.browser.operator", IdentityRoles.Operator),
            ("r8.browser.approver", IdentityRoles.Operator),
            ("r8.browser.four-eyes-operator", IdentityRoles.Operator),
            ("r8.browser.four-eyes-supervisor", IdentityRoles.Supervisor)
        };
        foreach (var (name, role) in identities)
        {
            var user = new StaffUser { UserName = name };
            if (!(await users.CreateAsync(user, password)).Succeeded || !(await users.AddToRoleAsync(user, role)).Succeeded)
                throw new InvalidOperationException($"Could not provision R8 fixture identity {name}.");
            if (!await staff.GrantTenantAsync(user.Id, Tenant, deadline.Token))
                throw new InvalidOperationException($"Could not grant the fixture tenant to {name}.");
            if (name == "r8.browser.four-eyes-operator")
                await R8FourEyesFixture.PrepareAsync(scope.ServiceProvider, Tenant, $"staff:{user.Id:D}", deadline.Token);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
