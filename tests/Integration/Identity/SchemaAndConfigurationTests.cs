using System.Net;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Identity.Staff;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Integration.Tests.Identity;

/// <summary>The marrow_identity migration through the single executor, least privilege and write-once markers.</summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IdentitySchemaTests(IdentityDatabase db)
{
    [Fact]
    public async Task The_single_executor_applies_identity_after_timeline_and_is_idempotent()
    {
        var first = db.FirstMigrationOutput;
        var timeline = first.IndexOf("module marrow_timeline: applying 0001_timeline_journal", StringComparison.Ordinal);
        var identity = first.IndexOf("module marrow_identity: applying 0001_staff_guest_identity", StringComparison.Ordinal);
        Assert.True(timeline >= 0 && identity > timeline, first);
        Assert.Contains("0001_staff_guest_identity", await db.ScalarAsync<string>("SELECT string_agg(version, ',') FROM marrow_identity.schema_migrations") ?? "");

        var second = await db.RunMigratorAsync();
        Assert.Contains("0 applied", second, StringComparison.Ordinal);

        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_constraint c JOIN pg_class a ON a.oid = c.conrelid JOIN pg_class b ON b.oid = c.confrelid " +
            "WHERE c.contype = 'f' AND a.relnamespace <> b.relnamespace"));
    }

    [Fact]
    public async Task The_runtime_login_has_least_privilege()
    {
        await using var app = NpgsqlDataSource.Create(db.AppConnectionString);
        foreach (var sql in new[]
        {
            "DELETE FROM marrow_identity.staff_users",
            "DELETE FROM marrow_identity.staff_sessions",
            "DELETE FROM marrow_identity.guest_principals",
            "TRUNCATE marrow_identity.staff_refresh_tokens CASCADE",
            "UPDATE marrow_identity.staff_refresh_tokens SET token_hash = decode(repeat('00', 32), 'hex')",
            "UPDATE marrow_identity.staff_sessions SET user_id = user_id",
            "UPDATE marrow_identity.guest_sessions SET secret_hash = secret_hash",
            "UPDATE marrow_identity.staff_users SET id = id",
            "SELECT 1 FROM marrow_timeline.histories",
            "CREATE TABLE marrow_identity.x (a int)",
        })
        {
            await using var command = app.CreateCommand(sql);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    /// <summary>
    /// The executor provisions one non-superuser login per host with only the module roles it needs, converges on re-run,
    /// never prints a password and refuses placeholders. Hosts therefore never need the migration (superuser) credentials.
    /// </summary>
    [Fact]
    public async Task The_executor_provisions_least_privilege_host_logins_without_printing_passwords()
    {
        foreach (var output in new[] { db.FirstMigrationOutput, await db.RunMigratorAsync() })
        {
            Assert.Contains("runtime login sands_api: provisioned (marrow_identity_runtime)", output, StringComparison.Ordinal);
            Assert.Contains("runtime login sands_realtime: provisioned (marrow_identity_runtime)", output, StringComparison.Ordinal);
            // Spec 07 task 7.5 closed the Worker's mapping: V2 Timeline projection (and the future Replay mode) only.
            Assert.Contains("runtime login sands_worker: provisioned (marrow_timeline_runtime revenant_replay_reader revenant_read_projector)", output, StringComparison.Ordinal);
            foreach (var password in db.RuntimePasswords)
            {
                Assert.DoesNotContain(password, output, StringComparison.Ordinal);
            }
        }

        foreach (var login in new[] { IdentityDatabase.ApiLogin, IdentityDatabase.RealtimeLogin, IdentityDatabase.WorkerLogin })
        {
            Assert.Equal("login,inherit", await db.ScalarAsync<string>(
                "SELECT concat_ws(',', CASE WHEN rolcanlogin THEN 'login' END, CASE WHEN rolsuper THEN 'super' END, " +
                "CASE WHEN rolcreaterole THEN 'createrole' END, CASE WHEN rolcreatedb THEN 'createdb' END, " +
                "CASE WHEN rolreplication THEN 'replication' END, CASE WHEN rolbypassrls THEN 'bypassrls' END, " +
                "CASE WHEN rolinherit THEN 'inherit' END) FROM pg_roles WHERE rolname = @n", ("n", login)));
        }
        const string memberships =
            "SELECT coalesce(string_agg(g.rolname, ',' ORDER BY g.rolname), '') FROM pg_auth_members m " +
            "JOIN pg_roles g ON g.oid = m.roleid JOIN pg_roles u ON u.oid = m.member WHERE u.rolname = @n";
        Assert.Equal("marrow_identity_runtime", await db.ScalarAsync<string>(memberships, ("n", IdentityDatabase.ApiLogin)));
        Assert.Equal("marrow_identity_runtime", await db.ScalarAsync<string>(memberships, ("n", IdentityDatabase.RealtimeLogin)));
        Assert.Equal("marrow_timeline_runtime,revenant_read_projector,revenant_replay_reader", await db.ScalarAsync<string>(memberships, ("n", IdentityDatabase.WorkerLogin)));

        await using var worker = NpgsqlDataSource.Create(db.WorkerConnectionString);
        await using (var ping = worker.CreateCommand("SELECT 1"))
        {
            Assert.Equal(1, await ping.ExecuteScalarAsync());
        }
        foreach (var (connectionString, sql) in new[]
        {
            (db.WorkerConnectionString, "SELECT 1 FROM marrow_identity.guest_sessions"),
            (db.WorkerConnectionString, "DELETE FROM revenant.payment"),
            (db.WorkerConnectionString, "UPDATE revenant.outbox SET status = 'pending'"),
            (db.WorkerConnectionString, "CREATE TABLE public.worker_probe (a int)"),
            (db.RealtimeConnectionString, "SELECT 1 FROM marrow_timeline.histories"),
            (db.RealtimeConnectionString, "DELETE FROM marrow_identity.guest_sessions"),
            (db.RealtimeConnectionString, "CREATE SCHEMA realtime_probe"),
            (db.AppConnectionString, "INSERT INTO marrow_identity.schema_migrations (version, checksum) VALUES ('9999_x', 'x')"),
            (db.AppConnectionString, "CREATE TABLE public.api_probe (a int)"),
        })
        {
            await using var source = NpgsqlDataSource.Create(connectionString);
            await using var command = source.CreateCommand(sql);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        var (placeholderExit, placeholderOutput) = await db.TryRunMigratorAsync("API_DB_PASSWORD=REPLACE_ME_API_DB");
        Assert.NotEqual(0, placeholderExit);
        Assert.Contains("API_DB_PASSWORD is still the .env.example placeholder", placeholderOutput, StringComparison.Ordinal);
        var (shortExit, shortOutput) = await db.TryRunMigratorAsync("WORKER_DB_PASSWORD=abc123");
        Assert.NotEqual(0, shortExit);
        Assert.Contains("WORKER_DB_PASSWORD must be at least 24 characters", shortOutput, StringComparison.Ordinal);
        // The refused runs changed nothing: the hosts' logins still work.
        await using var api = NpgsqlDataSource.Create(db.AppConnectionString);
        await using var stillWorks = api.CreateCommand("SELECT count(*) FROM marrow_identity.guest_sessions");
        Assert.IsType<long>(await stillWorks.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Revocation_and_consumption_markers_are_write_once_even_for_the_owner()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync();
        var (browser, access, _) = await Flows.LoginAsync(factory, staff);
        var sessionId = Flows.SessionId(access);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/staff/refresh", csrfCookie: Flows.StaffCsrf)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/staff/logout", csrfCookie: Flows.StaffCsrf)).Status);
        var guest = factory.NewBrowser();
        var guestId = Guid.Parse((await guest.PostAsync("/auth/guest/start")).Str("subject")["guest:".Length..]);
        await guest.PostAsync("/auth/guest/signout", csrfCookie: Flows.GuestCsrf);

        await AssertWriteOnceAsync("UPDATE marrow_identity.staff_sessions SET revoked_at = NULL, revoke_reason = NULL WHERE id = @id", ("id", sessionId));
        await AssertWriteOnceAsync("UPDATE marrow_identity.staff_sessions SET revoke_reason = 'other' WHERE id = @id", ("id", sessionId));
        await AssertWriteOnceAsync("UPDATE marrow_identity.staff_refresh_tokens SET consumed_at = NULL WHERE session_id = @id AND consumed_at IS NOT NULL", ("id", sessionId));
        await AssertWriteOnceAsync("UPDATE marrow_identity.guest_sessions SET revoked_at = NULL, revoke_reason = NULL WHERE guest_id = @id", ("id", guestId));
    }

    [Fact]
    public async Task Constraints_reject_unknown_roles_and_mfa_without_a_key()
    {
        await using var factory = new IdentityApiFactory(db);
        var staff = await factory.CreateStaffAsync();
        await using var badRole = db.Owner.CreateCommand("INSERT INTO marrow_identity.staff_user_roles (user_id, role) VALUES (@u, 'admin')");
        badRole.Parameters.AddWithValue("u", staff.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => badRole.ExecuteNonQueryAsync())).SqlState);

        await using var noKey = db.Owner.CreateCommand("UPDATE marrow_identity.staff_users SET two_factor_enabled = true WHERE id = @u");
        noKey.Parameters.AddWithValue("u", staff.Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => noKey.ExecuteNonQueryAsync())).SqlState);

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
        var result = await users.CreateAsync(new StaffUser { UserName = staff.UserName.ToUpperInvariant() }, "Aa1!" + Guid.NewGuid().ToString("N"));
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "DuplicateUserName");
    }

    private async Task AssertWriteOnceAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = db.Owner.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("MI001", ex.SqlState);
    }
}

/// <summary>No default secret in production; Development-only, non-committed seeding.</summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class IdentityConfigurationTests(IdentityDatabase db)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("REPLACE_ME_IDENTITY_0123456789abcdefghijkl")]
    [InlineData("change-me-change-me-change-me-12345678")]
    [InlineData("short-secret")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("abababababababababababababababababababab")]
    public void Key_material_refuses_missing_short_placeholder_and_low_variety_secrets_outside_development(string? secret)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => IdentityKeyMaterial.Create(secret, isDevelopment: false));
        Assert.Contains("Identity:MasterSecret", ex.Message, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Key_material_accepts_a_strong_secret_and_only_development_may_generate_one()
    {
        Assert.False(IdentityKeyMaterial.Create(IdentityApiFactory.NewMasterSecret(), isDevelopment: false).IsEphemeral);
        Assert.True(IdentityKeyMaterial.Create(null, isDevelopment: true).IsEphemeral);
        var a = IdentityKeyMaterial.Create(null, isDevelopment: true);
        var b = IdentityKeyMaterial.Create(null, isDevelopment: true);
        Assert.NotEqual(a.GuestCookieMac("x"), b.GuestCookieMac("x"));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "REPLACE_ME_IDENTITY_0123456789abcdefghijkl")]
    public void A_production_host_without_a_real_secret_fails_to_start(bool withSecret, string? configured)
    {
        var extra = configured is null ? null : new Dictionary<string, string?> { ["Identity:MasterSecret"] = configured };
        using var factory = new IdentityApiFactory(db, environment: "Production", withSecret: withSecret, extraSettings: extra);
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Identity:MasterSecret", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_production_host_with_a_real_secret_starts_and_never_seeds_users()
    {
        var name = $"prod-op-{Guid.NewGuid():N}@dev.sands.local";
        await using var factory = new IdentityApiFactory(db, environment: "Production", extraSettings: new()
        {
            ["Identity:DevSeed:OperatorUserName"] = name,
            ["Identity:DevSeed:OperatorPassword"] = "Aa1!" + Guid.NewGuid().ToString("N"),
        });
        var browser = factory.NewBrowser();
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/auth/guest/start")).Status);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.staff_users WHERE user_name = @n", ("n", name)));
    }

    [Fact]
    public async Task Development_seeds_distinct_operator_and_supervisor_only_from_configured_credentials()
    {
        var operatorName = $"op-{Guid.NewGuid():N}@dev.sands.local";
        var supervisorName = $"sup-{Guid.NewGuid():N}@dev.sands.local";
        var operatorPassword = "Aa1!" + Guid.NewGuid().ToString("N");
        var supervisorPassword = "Bb2@" + Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?>
        {
            ["Identity:DevSeed:OperatorUserName"] = operatorName,
            ["Identity:DevSeed:OperatorPassword"] = operatorPassword,
            ["Identity:DevSeed:SupervisorUserName"] = supervisorName,
            ["Identity:DevSeed:SupervisorPassword"] = supervisorPassword,
        };

        // No Identity:MasterSecret: Development generates an ephemeral key and says so.
        await using (var first = new IdentityApiFactory(db, environment: "Development", withSecret: false, extraSettings: settings))
        {
            var op = first.NewBrowser();
            var opLogin = await op.PostAsync("/auth/staff/login", new { userName = operatorName, password = operatorPassword });
            Assert.Equal(HttpStatusCode.OK, opLogin.Status);
            Assert.Equal(["operator"], opLogin.Roles);
            var sup = first.NewBrowser();
            var supLogin = await sup.PostAsync("/auth/staff/login", new { userName = supervisorName, password = supervisorPassword });
            Assert.Equal(["supervisor"], supLogin.Roles);
            Assert.NotEqual(opLogin.Str("subject"), supLogin.Str("subject"));

            // The credentials do not cross over.
            Assert.Equal(HttpStatusCode.Unauthorized, (await first.NewBrowser().PostAsync("/auth/staff/login", new { userName = operatorName, password = supervisorPassword })).Status);
            Assert.Contains(first.Logs.Lines, l => l.EventId == 3018);
        }

        // A restart is idempotent.
        await using (var second = new IdentityApiFactory(db, environment: "Development", withSecret: false, extraSettings: settings))
        {
            Assert.Equal(HttpStatusCode.OK, (await second.NewBrowser().PostAsync("/auth/staff/login", new { userName = operatorName, password = operatorPassword })).Status);
            Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.staff_users WHERE user_name IN (@a, @b)", ("a", operatorName), ("b", supervisorName)));
        }

        // Without a configured password nothing is created: the repository ships no demo credential.
        var ghost = $"ghost-{Guid.NewGuid():N}@dev.sands.local";
        await using var unseeded = new IdentityApiFactory(db, environment: "Development", withSecret: false, extraSettings: new() { ["Identity:DevSeed:OperatorUserName"] = ghost });
        Assert.Equal(HttpStatusCode.OK, (await unseeded.NewBrowser().PostAsync("/auth/guest/start")).Status);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM marrow_identity.staff_users WHERE user_name = @n", ("n", ghost)));
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            parts.Add(e.Message);
        }

        return string.Join(" | ", parts);
    }
}
