using System.Net;
using System.Security.Cryptography;
using Integration.Tests.Support;
using Marrow.Infrastructure.Identity.Staff;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Realtime;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Realtime;
using Marrow.Timeline;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Api;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Projection;
using Revenant.Timeline;
using Xunit;

namespace Integration.Tests.RevenantLedger;

/// <summary>
/// Spec 07 task 7.4 through the real <c>Hosts.Api</c> and <c>Hosts.Realtime</c> compositions: real staff logins with
/// server-side tenant memberships, real TOTP step-up, the production Revenant registrations, real PostgreSQL 18 migrated by
/// <c>scripts/db/migrate.sh</c>, a ledger login that is only a member of <c>revenant_ledger_writer</c> (API) and one that is only
/// a member of <c>revenant_replay_reader</c> (hub). No fixture routes, no mocks.
/// **Validates: Requirements R07.05, R07.07, R07.08, R07.13**
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RevenantCaseApiTests(IdentityDatabase db)
{
    private const string Tenant = "bank-case-it";
    private const string OtherTenant = "bank-case-other";

    private async Task<string> LoginForRoleAsync(string login, string role)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using (var cmd = db.Owner.CreateCommand(
            $"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{login}') THEN
                CREATE ROLE {login} LOGIN IN ROLE {role};
              END IF;
            END $$;
            ALTER ROLE {login} PASSWORD '{password}';
            """))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Username = login, Password = password }.ConnectionString;
    }

    private async Task<IdentityApiFactory> ApiAsync(string? secret = null) => new(db, secret, extraSettings: new()
    {
        ["ConnectionStrings:RevenantLedger"] = await LoginForRoleAsync("revenant_case_api_it", "revenant_ledger_writer"),
    });

    private sealed record Staff(TestStaff Account, TestBrowser Browser, string Access, string Subject);

    private static async Task<Staff> StaffAsync(IdentityApiFactory api, string role, string? tenant = Tenant)
    {
        var account = await api.CreateStaffAsync(role, tenant);
        var (browser, access, _) = await Flows.LoginAsync(api, account);
        return new Staff(account, browser, access, "staff:" + account.Id.ToString("D"));
    }

    /// <summary>
    /// The owner opens a run and submits a sanctions-flagged payment over the API; the production LiveOps screening step
    /// (task 7.5, <see cref="PaymentLedgerStore.ScreenBatchAsync"/>, what the Worker's screening loop runs) holds it under policy v7.
    /// No HTTP route moves payments.
    /// </summary>
    private static async Task<(Guid Run, string Intent)> HeldRunAsync(IdentityApiFactory api, Staff owner, long amount)
    {
        var runId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.Browser.PostAsync("/revenant/ledger/runs", new { seed = 42, runId }, bearer: owner.Access)).Status);
        var submitted = await owner.Browser.PostAsync($"/revenant/ledger/runs/{runId}/payments", new
        {
            intentId = "HV-1", sequence = 1, eventTimeUtcMs = 1_000, sourceBankId = "BANK-A", sourceAccountId = "BANK-A/ACC-1",
            destBankId = "BANK-D", destAccountId = "BANK-D/ACC-1", currency = "USD", amountMinor = amount, truth = "Legitimate",
            newBeneficiary = false, sanctionsHit = true,
        }, bearer: owner.Access);
        Assert.Equal(HttpStatusCode.Created, submitted.Status);
        var store = api.Services.GetRequiredService<PaymentLedgerStore>();
        var screened = Assert.Single(await store.ScreenBatchAsync(Tenant, 10, "it-screening", default), o => o.RunId.Value == runId);
        Assert.Equal((PaymentStatus.Held, "sanctions_hit", 3L), (screened.Status, screened.Reason, screened.Version));
        return (runId, "HV-1");
    }

    [Fact]
    public async Task Two_real_tenant_bearing_staff_sessions_release_a_high_value_hold_only_with_distinct_mfa_authority()
    {
        await using var api = await ApiAsync();
        var op = await StaffAsync(api, IdentityRoles.Operator);
        var sup = await StaffAsync(api, IdentityRoles.Supervisor);
        var (runId, intent) = await HeldRunAsync(api, op, 1_500_000);
        var cases = $"/revenant/ledger/runs/{runId}/cases/{intent}";

        // Before the supervisor is enrolled on the run, they are an outsider like anyone else.
        Assert.Equal(HttpStatusCode.Forbidden, (await sup.Browser.GetAsync(cases, bearer: sup.Access)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await op.Browser.PostAsync($"/revenant/ledger/runs/{runId}/staff",
            new { subjectId = sup.Subject, role = "supervisor" }, bearer: op.Access)).Status);

        var read = await op.Browser.GetAsync(cases, bearer: op.Access);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal(("Open", "Held", 2), (read.Str("status"), read.Str("paymentStatus"), read.Json.GetProperty("requiredApprovals").GetInt32()));
        Assert.True(read.Json.GetProperty("screening").GetProperty("isMatch").GetBoolean());

        // No MFA step-up: refused with the identity layer's code; nothing changes.
        var noMfa = await op.Browser.PostAsync($"{cases}/approve", new { expectedVersion = 1, commandId = "op-approve" }, bearer: op.Access);
        Assert.Equal((HttpStatusCode.Forbidden, "mfa_required"), (noMfa.Status, noMfa.Code));

        await Flows.EnrollAsync(op.Browser, op.Access, op.Account); // enrollment is a fresh step-up of this session
        // Client-supplied identity, role and tenant fields are not part of the contract and change nothing.
        var first = await op.Browser.PostAsync($"{cases}/approve",
            new { expectedVersion = 1, commandId = "op-approve", subjectId = sup.Subject, role = "supervisor", tenantId = OtherTenant }, bearer: op.Access);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(("AwaitingSecondApproval", "Held"), (first.Str("status"), first.Str("paymentStatus")));
        Assert.Equal(op.Subject, first.Json.GetProperty("approvals")[0].GetProperty("subjectId").GetString());
        Assert.Equal("operator", first.Json.GetProperty("approvals")[0].GetProperty("role").GetString());

        var self = await op.Browser.PostAsync($"{cases}/approve", new { expectedVersion = 2, commandId = "op-again" }, bearer: op.Access);
        Assert.Equal((HttpStatusCode.Forbidden, "distinct_supervisor_required"), (self.Status, self.Code));
        var supNoMfa = await sup.Browser.PostAsync($"{cases}/approve", new { expectedVersion = 2, commandId = "sup-approve" }, bearer: sup.Access);
        Assert.Equal((HttpStatusCode.Forbidden, "mfa_required"), (supNoMfa.Status, supNoMfa.Code));
        var stale = await sup.Browser.PostAsync($"{cases}/assign", new { expectedVersion = 1, commandId = "sup-assign" }, bearer: sup.Access);
        Assert.Equal((HttpStatusCode.Conflict, "stale_version"), (stale.Status, stale.Code));

        await Flows.EnrollAsync(sup.Browser, sup.Access, sup.Account);
        var second = await sup.Browser.PostAsync($"{cases}/approve", new { expectedVersion = 2, commandId = "sup-approve" }, bearer: sup.Access);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        // Facts: 1 intent, 2 screening, 3 first approval, 4 release.
        Assert.Equal(("Released", "Approved", 4L), (second.Str("status"), second.Str("paymentStatus"), second.Json.GetProperty("factSequence").GetInt64()));
        Assert.Equal(sup.Subject, second.Json.GetProperty("approvals")[1].GetProperty("subjectId").GetString());
        var replay = await sup.Browser.PostAsync($"{cases}/approve", new { expectedVersion = 2, commandId = "sup-approve" }, bearer: sup.Access);
        Assert.True(replay.Json.GetProperty("replayed").GetBoolean());

        var payment = await op.Browser.GetAsync($"/revenant/ledger/runs/{runId}/payments/{intent}", bearer: op.Access);
        Assert.Equal("Approved", payment.Str("status"));
        Assert.Equal(sup.Subject, payment.Json.GetProperty("trail")[3].GetProperty("actor").GetString());

        // Comments: run staff only, attributed to the verified actor.
        var comment = await sup.Browser.PostAsync($"{cases}/comments", new { body = "Verified with the receiving bank.", commandId = "c-1", authorSubject = "staff:forged" }, bearer: sup.Access);
        Assert.Equal(HttpStatusCode.Created, comment.Status);
        Assert.Equal(sup.Subject, comment.Json.GetProperty("comments")[0].GetProperty("authorSubject").GetString());
        var sameTenantOutsider = await StaffAsync(api, IdentityRoles.Supervisor);
        var otherTenant = await StaffAsync(api, IdentityRoles.Supervisor, OtherTenant);
        var tenantless = await StaffAsync(api, IdentityRoles.Supervisor, tenant: null);
        foreach (var outsider in new[] { sameTenantOutsider, otherTenant, tenantless })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Browser.PostAsync($"{cases}/comments", new { body = "x", commandId = "c-x" }, bearer: outsider.Access)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Browser.GetAsync(cases, bearer: outsider.Access)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Browser.PostAsync($"/revenant/ledger/runs/{runId}/staff",
                new { subjectId = outsider.Subject, role = "supervisor" }, bearer: outsider.Access)).Status);
        }
        // A tenantless staff member cannot open a run, and a supervisor of another tenant gets the other tenant's run namespace only.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantless.Browser.PostAsync("/revenant/ledger/runs", new { seed = 1 }, bearer: tenantless.Access)).Status);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM revenant.case_comment WHERE scenario_run_id = @r", ("r", runId)));
        Assert.Equal(2L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM revenant.case_approval a JOIN revenant.review_case c ON c.id = a.case_id WHERE c.scenario_run_id = @r", ("r", runId)));

        // Task 7.5: LiveOps settlement completes held -> decision -> settlement, the reviewer verifies the audit chain over HTTP,
        // and the run's V2 Timeline source reconciles with the live ledger and verifies.
        var store = api.Services.GetRequiredService<PaymentLedgerStore>();
        var settled = Assert.Single((await store.SettleBatchAsync(Tenant, "system:settlement-worker", 50, "it-settle", default)).Outcomes,
            o => o.RunId.Value == runId);
        Assert.Equal(PaymentStatus.Settled, settled.Status);
        var trail = (await op.Browser.GetAsync($"/revenant/ledger/runs/{runId}/payments/{intent}", bearer: op.Access)).Json.GetProperty("trail");
        Assert.Equal(["Initiated", "Screened", "Held", "Approved", "Cleared", "Settled"], trail.EnumerateArray().Select(t => t.GetProperty("toStatus").GetString()));
        var audit = await sup.Browser.GetAsync($"/revenant/ledger/runs/{runId}/audit/verification", bearer: sup.Access);
        Assert.Equal(HttpStatusCode.OK, audit.Status);
        Assert.True(audit.Json.GetProperty("chainIntact").GetBoolean());
        Assert.Equal("database-tamper-evident; not externally notarized", audit.Str("assurance"));
        Assert.True(audit.Json.GetProperty("events").GetInt64() >= 5);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Browser.GetAsync($"/revenant/ledger/runs/{runId}/audit/verification", bearer: op.Access)).Status);

        // The run is closed for new financial facts before its source is verified (the payment is Settled, nothing is in flight).
        Assert.True((await store.CloseRunAsync(Tenant, "system:close", new ScenarioRunId(runId), "it-close", default)).IsSuccess);
        var projectionLogin = await LoginForRoleAsync("revenant_projection_it", "revenant_replay_reader");
        await using (var grant = db.Owner.CreateCommand("GRANT marrow_timeline_runtime TO revenant_projection_it"))
        {
            await grant.ExecuteNonQueryAsync();
        }
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(projectionLogin));
        services.AddSingleton(new RevenantLedgerOptions());
        services.AddMarrowTimeline(FinanceLedgerTimelineVersions.All, FinanceLedgerTimelineVersions.CheckpointInterval)
            .AddRevenantLedgerProjection(new ConfigurationBuilder().Build(), runWorker: false);
        await using var projection = services.BuildServiceProvider();
        var verified = await projection.GetRequiredService<LedgerProjector>().VerifySourceAsync(Tenant, new ScenarioRunId(runId), default);
        Assert.True(verified.IsSuccess, verified.IsSuccess ? "" : verified.Error.ToString());
        Assert.True(verified.Value.Reconciled, string.Join("; ", verified.Value.Mismatches));
        Assert.Equal((HistoryStatus.Verified, 5L), (verified.Value.Projection.Status, verified.Value.Projection.ProjectedPosition));
    }

    [Fact]
    public async Task Revoked_tenant_or_run_membership_is_denied_at_the_api_and_on_the_hub_on_the_next_check()
    {
        var secret = IdentityApiFactory.NewMasterSecret();
        await using var api = await ApiAsync(secret);
        await using var hub = new RealtimeHostFactory(db, secret, new()
        {
            ["ConnectionStrings:RevenantLedger"] = await LoginForRoleAsync("revenant_case_hub_it", "revenant_replay_reader"),
            ["Realtime:AccessRevalidationSeconds"] = "1",
        }, registerDomainPolicies: false, registerProbes: false);
        var owner = await StaffAsync(api, IdentityRoles.Operator);
        var tenantRevoked = await StaffAsync(api, IdentityRoles.Supervisor);
        var runRevoked = await StaffAsync(api, IdentityRoles.Supervisor);
        var (runId, intent) = await HeldRunAsync(api, owner, 400_000);
        foreach (var member in new[] { tenantRevoked, runRevoked })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await owner.Browser.PostAsync($"/revenant/ledger/runs/{runId}/staff",
                new { subjectId = member.Subject, role = "supervisor" }, bearer: owner.Access)).Status);
        }
        var stream = $"{RevenantResourceStream}:{runId:N}";
        var cases = $"/revenant/ledger/runs/{runId}/cases/{intent}";

        await using var tenantConn = hub.Connect(accessToken: tenantRevoked.Access, origin: null);
        await using var runConn = hub.Connect(accessToken: runRevoked.Access, origin: null);
        await using var ownerConn = hub.Connect(accessToken: owner.Access, origin: null);
        foreach (var conn in new[] { tenantConn, runConn, ownerConn })
        {
            await conn.StartAsync();
            var reply = await conn.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, stream);
            Assert.True(reply.Ok, reply.Code);
            Assert.Equal("2", reply.Snapshot!.Sequence); // last committed fact of the run (intent, then its screening fact)
        }
        // A staff member of the same tenant who is not on the run cannot subscribe.
        var outsider = await StaffAsync(api, IdentityRoles.Supervisor);
        await using (var outsiderConn = hub.Connect(accessToken: outsider.Access, origin: null))
        {
            await outsiderConn.StartAsync();
            Assert.Equal(RealtimeFailure.Forbidden, (await outsiderConn.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, stream)).Code);
        }
        Assert.Equal(HttpStatusCode.OK, (await tenantRevoked.Browser.GetAsync(cases, bearer: tenantRevoked.Access)).Status);
        Assert.Equal(HttpStatusCode.OK, (await runRevoked.Browser.GetAsync(cases, bearer: runRevoked.Access)).Status);

        // Revoke: one tenant membership (identity, server-side) and one run membership (owner, over the API).
        await using (var scope = api.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<StaffAuthService>().RevokeTenantAsync(tenantRevoked.Account.Id, Tenant, default));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Browser.PostAsync($"/revenant/ledger/runs/{runId}/staff/{runRevoked.Subject}/revoke", bearer: owner.Access)).Status);

        // API: the same still-unexpired access tokens are refused on the very next request.
        foreach (var revoked in new[] { tenantRevoked, runRevoked })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await revoked.Browser.GetAsync(cases, bearer: revoked.Access)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await revoked.Browser.PostAsync($"{cases}/comments", new { body = "x", commandId = "late" }, bearer: revoked.Access)).Status);
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.Browser.GetAsync(cases, bearer: owner.Access)).Status);

        // Hub: periodic revalidation removes both subscriptions (event 3211), and a new subscribe is refused; the owner keeps access.
        await Eventually.TrueAsync(() => hub.Logs.Lines.Count(l => l.EventId == 3211) >= 2, "both revoked subscriptions are removed", 10_000);
        Assert.Equal(RealtimeFailure.Forbidden, (await tenantConn.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, stream)).Code);
        Assert.Equal(RealtimeFailure.Forbidden, (await runConn.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, stream)).Code);
        Assert.True((await ownerConn.InvokeAsync<RealtimeReply>(RealtimeWire.SubscribeMethod, stream)).Ok);
        Assert.Equal(HubConnectionState.Connected, ownerConn.State);
    }

    private const string RevenantResourceStream = Revenant.Api.RevenantRunStreamSource.Kind;
}
