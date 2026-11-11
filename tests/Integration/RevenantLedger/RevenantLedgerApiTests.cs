using System.Net;
using System.Security.Cryptography;
using Integration.Tests.Support;
using Npgsql;
using Xunit;

namespace Integration.Tests.RevenantLedger;

/// <summary>
/// Spec 07 task 7.2 through the real <c>Hosts.Api</c> composition: real staff login (access JWT), the production Revenant
/// registration, a real PostgreSQL 18 migrated by <c>scripts/db/migrate.sh</c>, and a dedicated login that is only a member of
/// <c>revenant_ledger_writer</c>. No fixture routes and no mocks. **Validates: Requirements R07.02, R07.08, R07.09, R07.12**
/// </summary>
[Collection(IdentityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RevenantLedgerApiTests(IdentityDatabase db)
{
    private const string Tenant = "demo-bank-it";
    private const string WriterLogin = "revenant_api_it";

    private async Task<IdentityApiFactory> FactoryAsync(bool configured = true)
    {
        var settings = new Dictionary<string, string?>();
        if (configured)
        {
            settings["ConnectionStrings:RevenantLedger"] = await WriterConnectionStringAsync();
            settings["Revenant:Ledger:TenantId"] = Tenant;
        }
        return new IdentityApiFactory(db, extraSettings: settings);
    }

    private async Task<string> WriterConnectionStringAsync()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using (var cmd = db.Owner.CreateCommand(
            $"""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{WriterLogin}') THEN
                CREATE ROLE {WriterLogin} LOGIN IN ROLE revenant_ledger_writer;
              END IF;
            END $$;
            ALTER ROLE {WriterLogin} PASSWORD '{password}';
            """))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(db.AppConnectionString) { Username = WriterLogin, Password = password }.ConnectionString;
    }

    private static async Task<(TestBrowser Browser, string Access, string Subject)> StaffAsync(IdentityApiFactory factory, string role = "operator")
    {
        // Task 7.4: ledger staff are tenant-bearing (server-side tenant membership read on every request).
        var staff = await factory.CreateStaffAsync(role, Tenant);
        var (browser, access, _) = await Flows.LoginAsync(factory, staff);
        return (browser, access, "staff:" + staff.Id);
    }

    private static object Payment(string intentId, long sequence, long time = 1_000, long amount = 12_500, string currency = "USD") => new
    {
        intentId,
        sequence,
        eventTimeUtcMs = time,
        sourceBankId = "BANK-A",
        sourceAccountId = "BANK-A/ACC-1",
        destBankId = "BANK-B",
        destAccountId = "BANK-B/ACC-1",
        currency,
        amountMinor = amount,
        truth = "Legitimate",
        newBeneficiary = false,
        sanctionsHit = false,
    };

    [Fact]
    public async Task Staff_open_a_run_and_submit_idempotent_payments_and_every_effect_is_in_the_database()
    {
        await using var factory = await FactoryAsync();
        var (browser, access, subject) = await StaffAsync(factory);
        var runId = Guid.NewGuid();

        // Client-supplied tenant/role/owner fields are ignored: they are not part of the contract.
        var opened = await browser.PostAsync("/revenant/ledger/runs", new { seed = 42, runId, tenantId = "evil", role = "supervisor", ownerSubject = "staff:x" }, bearer: access);
        Assert.Equal(HttpStatusCode.Created, opened.Status);
        Assert.Equal(runId.ToString(), opened.Str("runId"));
        Assert.False(opened.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal("revenant-finance-reducer/2", opened.Str("financeVersion"));
        var banks = opened.Json.GetProperty("banks");
        Assert.Equal(6, banks.GetArrayLength());
        Assert.All(banks.EnumerateArray(), b => Assert.Equal(b.GetProperty("openingMinor").GetInt64(), b.GetProperty("postedMinor").GetInt64()));
        Assert.Equal((Tenant, subject), (
            await db.ScalarAsync<string>("SELECT tenant_id FROM revenant.scenario_run WHERE id = @r", ("r", runId)),
            await db.ScalarAsync<string>("SELECT owner_subject_id FROM revenant.scenario_run WHERE id = @r", ("r", runId))));

        var reopened = await browser.PostAsync("/revenant/ledger/runs", new { seed = 42, runId }, bearer: access);
        Assert.Equal(HttpStatusCode.OK, reopened.Status);
        Assert.True(reopened.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsync("/revenant/ledger/runs", new { seed = 43, runId }, bearer: access)).Status);

        var path = $"/revenant/ledger/runs/{runId}/payments";
        var first = await browser.PostAsync(path, Payment("P-1", 1), bearer: access);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal("Initiated", first.Str("status"));
        Assert.Equal(1, first.Json.GetProperty("version").GetInt64());
        Assert.Equal(1, first.Json.GetProperty("factSequence").GetInt64());
        Assert.Equal(subject, first.Json.GetProperty("trail")[0].GetProperty("actor").GetString());

        var replay = await browser.PostAsync(path, Payment("P-1", 1), bearer: access);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.True(replay.Json.GetProperty("replayed").GetBoolean());
        Assert.Equal(first.Str("paymentId"), replay.Str("paymentId"));

        var conflict = await browser.PostAsync(path, Payment("P-1", 1, amount: 99), bearer: access);
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal("conflicting_duplicate", conflict.Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await browser.PostAsync(path, Payment("P-0", 1), bearer: access)).Status);
        var eur = await browser.PostAsync(path, Payment("P-2", 2, currency: "EUR"), bearer: access);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "unsupported_currency"), (eur.Status, eur.Code));
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync(path, Payment("", 2), bearer: access)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync(path, new { intentId = "P-3" }, bearer: access)).Status);

        var read = await browser.GetAsync($"{path}/P-1", bearer: access);
        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal("Initiated", read.Str("status"));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"{path}/P-404", bearer: access)).Status);
        var run = await browser.GetAsync($"/revenant/ledger/runs/{runId}", bearer: access);
        Assert.Equal(HttpStatusCode.OK, run.Status);

        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r", ("r", runId)));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM revenant.finance_fact WHERE scenario_run_id = @r", ("r", runId)));
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM revenant.outbox o JOIN revenant.payment p ON o.aggregate_id = 'payment:' || replace(p.id::text, '-', '') WHERE p.scenario_run_id = @r",
            ("r", runId)));
        Assert.False(string.IsNullOrEmpty(await db.ScalarAsync<string>(
            "SELECT o.correlation_id FROM revenant.outbox o JOIN revenant.payment p ON o.aggregate_id = 'payment:' || replace(p.id::text, '-', '') WHERE p.scenario_run_id = @r",
            ("r", runId))));
    }

    [Fact]
    public async Task Another_staff_member_unknown_runs_and_anonymous_callers_are_refused_without_revealing_which()
    {
        await using var factory = await FactoryAsync();
        var (owner, ownerAccess, _) = await StaffAsync(factory);
        var (other, otherAccess, _) = await StaffAsync(factory, "supervisor");
        var runId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync("/revenant/ledger/runs", new { seed = 42, runId }, bearer: ownerAccess)).Status);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/revenant/ledger/runs/{runId}/payments", Payment("P-1", 1), bearer: ownerAccess)).Status);

        var unknown = Guid.NewGuid();
        var attempts = new[]
        {
            await other.GetAsync($"/revenant/ledger/runs/{runId}", bearer: otherAccess),
            await other.PostAsync($"/revenant/ledger/runs/{runId}/payments", Payment("P-2", 2), bearer: otherAccess),
            await other.GetAsync($"/revenant/ledger/runs/{runId}/payments/P-1", bearer: otherAccess),
            await owner.GetAsync($"/revenant/ledger/runs/{unknown}", bearer: ownerAccess),
            await owner.PostAsync($"/revenant/ledger/runs/{unknown}/payments", Payment("P-2", 2), bearer: ownerAccess),
        };
        Assert.All(attempts, a =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, a.Status);
            Assert.Equal("forbidden", a.Code);
        });
        // A supervisor role is not a bypass, and the other member's run id cannot be taken over by reopening it.
        var takeover = await other.PostAsync("/revenant/ledger/runs", new { seed = 42, runId }, bearer: otherAccess);
        Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), (takeover.Status, takeover.Code));

        var anonymous = factory.NewBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/revenant/ledger/runs/{runId}/payments", Payment("P-2", 2))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/revenant/ledger/runs/{runId}")).Status);

        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r", ("r", runId)));
    }

    [Fact]
    public async Task Without_a_configured_ledger_login_the_endpoints_answer_a_typed_503_and_never_fall_back_to_another_credential()
    {
        await using var factory = await FactoryAsync(configured: false);
        var (browser, access, _) = await StaffAsync(factory);

        var response = await browser.PostAsync("/revenant/ledger/runs", new { seed = 42 }, bearer: access);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "ledger_unavailable"), (response.Status, response.Code));
    }
}
