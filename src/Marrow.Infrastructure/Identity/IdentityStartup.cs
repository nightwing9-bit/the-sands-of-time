using Marrow.Infrastructure.Identity.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Marrow.Infrastructure.Identity;

/// <summary>
/// Startup chores that are not migrations. Warns when a Development run uses an ephemeral key and, in Development only,
/// seeds distinct operator and supervisor accounts whose passwords come from configuration (user secrets or environment).
/// With no configured password nothing is seeded: the repository contains no demo credential.
/// </summary>
internal sealed class IdentityStartup(
    IServiceProvider services,
    IHostEnvironment environment,
    IdentityKeyMaterial keys,
    IOptions<MarrowIdentityOptions> options,
    IdentityAuditLog audit) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (keys.IsEphemeral)
        {
            audit.EphemeralKey();
        }

        if (!environment.IsDevelopment())
        {
            return;
        }

        var seed = options.Value.DevSeed;
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var staff = scope.ServiceProvider.GetRequiredService<StaffAuthService>();
        await SeedAsync(users, staff, seed.OperatorUserName, seed.OperatorPassword, IdentityRoles.Operator, seed.TenantId, cancellationToken);
        await SeedAsync(users, staff, seed.SupervisorUserName, seed.SupervisorPassword, IdentityRoles.Supervisor, seed.TenantId, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedAsync(
        UserManager<StaffUser> users, StaffAuthService staff, string userName, string? password, string role, string tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            audit.DevSeed("skipped_no_password_configured", role);
            return;
        }

        if (await users.FindByNameAsync(userName) is { } existing)
        {
            await staff.GrantTenantAsync(existing.Id, tenantId, ct);
            audit.DevSeed("exists", role);
            return;
        }

        var user = new StaffUser { UserName = userName };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            // Codes only: descriptions could echo policy details; never the password or user name.
            throw new InvalidOperationException(
                "Development seed failed for role '" + role + "': " + string.Join(",", created.Errors.Select(e => e.Code)));
        }

        await users.AddToRoleAsync(user, role);
        await staff.GrantTenantAsync(user.Id, tenantId, ct);
        audit.DevSeed("created", role);
    }
}
