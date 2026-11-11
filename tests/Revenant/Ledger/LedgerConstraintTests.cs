using FsCheck;
using FsCheck.Fluent;
using Npgsql;
using Revenant.Contracts;
using Revenant.Scenario;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Ledger;

/// <summary>
/// Spec 07 task 7.1 (R07.01, R07.03, R07.10, R07.11, R07.12 schema side). Every write goes through raw SQL as the
/// least-privilege writer login with no application-side balance check, so any rejection observed here comes from
/// PostgreSQL itself.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LedgerConstraintTests(LedgerDatabase db, ITestOutputHelper output) : IClassFixture<LedgerDatabase>
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly string A1 = FridayRingScenario.CustomerAccountId(0, 1);
    private static readonly string A2 = FridayRingScenario.CustomerAccountId(0, 2);
    private static readonly string B1 = FridayRingScenario.CustomerAccountId(1, 1);
    private static readonly string C1 = FridayRingScenario.CustomerAccountId(2, 1);

    public enum GroupCase
    {
        Unbalanced,
        UnbalancedCreditShort,
        Empty,
        OneLeg,
        TotalNotPaymentAmount,
        LegOnForeignAccount,
        ReversedDirection,
    }

    [Fact]
    public async Task Migration_applies_cleanly_and_a_rerun_is_idempotent()
    {
        Assert.Contains("module revenant: applying 0001_ledger_core", db.MigrateOutput);
        var (exit, rerun) = await db.MigrateAgainAsync();
        Assert.Equal(0L, exit);
        Assert.Contains("0 applied", rerun);
    }

    [Fact]
    public async Task A_valid_settlement_group_commits_and_moves_posted_balances_with_the_total_conserved()
    {
        var run = await db.SeedRunAsync(TenantA);
        var before = await Ledger.PostedBalancesAsync(db.Writer, run);

        await Ledger.SettleAsync(db.Writer, run, "FR-0001", run.Account(A1), run.Account(B1), 12_345);

        var after = await Ledger.PostedBalancesAsync(db.Writer, run);
        Assert.Equal(before[run.Account(A1)] - 12_345, after[run.Account(A1)]);
        Assert.Equal(before[run.Account(B1)] + 12_345, after[run.Account(B1)]);
        Assert.Equal(run.TotalOpening, after.Values.Sum());
        Assert.Equal(18, after.Count);
    }

    [Fact]
    public async Task A_split_but_balanced_group_on_the_payment_accounts_commits()
    {
        var run = await db.SeedRunAsync(TenantA);
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, run.Tenant);
        var payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0002", run.Account(A1), run.Account(B1), 100);
        await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared", "Settled");
        var group = await Ledger.InsertGroupAsync(conn, run, payment, "Settlement");
        await Ledger.InsertLegAsync(conn, run, group, run.Account(A1), "Credit", 60);
        await Ledger.InsertLegAsync(conn, run, group, run.Account(A1), "Credit", 40);
        await Ledger.InsertLegAsync(conn, run, group, run.Account(B1), "Debit", 100);
        await tx.CommitAsync();
    }

    [Theory]
    [InlineData(GroupCase.Unbalanced)]
    [InlineData(GroupCase.UnbalancedCreditShort)]
    [InlineData(GroupCase.Empty)]
    [InlineData(GroupCase.OneLeg)]
    [InlineData(GroupCase.TotalNotPaymentAmount)]
    [InlineData(GroupCase.LegOnForeignAccount)]
    [InlineData(GroupCase.ReversedDirection)]
    public async Task The_database_rejects_an_invalid_settlement_group_at_commit(GroupCase shape)
    {
        var run = await db.SeedRunAsync(TenantA);
        var (src, dst, other) = (run.Account(A1), run.Account(B1), run.Account(C1));
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, run.Tenant);
        var payment = await Ledger.InsertPaymentAsync(conn, run, "FR-BAD", src, dst, 100);
        await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared", "Settled");
        var group = await Ledger.InsertGroupAsync(conn, run, payment, "Settlement");
        switch (shape)
        {
            case GroupCase.Unbalanced:
                await Ledger.InsertLegAsync(conn, run, group, src, "Credit", 100);
                await Ledger.InsertLegAsync(conn, run, group, dst, "Debit", 90);
                break;
            case GroupCase.UnbalancedCreditShort:
                await Ledger.InsertLegAsync(conn, run, group, src, "Credit", 90);
                await Ledger.InsertLegAsync(conn, run, group, dst, "Debit", 100);
                break;
            case GroupCase.Empty:
                break;
            case GroupCase.OneLeg:
                await Ledger.InsertLegAsync(conn, run, group, dst, "Debit", 100);
                break;
            case GroupCase.TotalNotPaymentAmount:
                await Ledger.InsertLegAsync(conn, run, group, src, "Credit", 50);
                await Ledger.InsertLegAsync(conn, run, group, dst, "Debit", 50);
                break;
            case GroupCase.LegOnForeignAccount:
                await Ledger.InsertLegAsync(conn, run, group, src, "Credit", 100);
                await Ledger.InsertLegAsync(conn, run, group, other, "Debit", 100);
                break;
            case GroupCase.ReversedDirection:
                await Ledger.InsertLegAsync(conn, run, group, src, "Debit", 100);
                await Ledger.InsertLegAsync(conn, run, group, dst, "Credit", 100);
                break;
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        output.WriteLine($"{shape}: {error.SqlState} {error.MessageText}");
        Assert.Equal("RV002", error.SqlState);
        await AssertNothingPersistedAsync(run);
    }

    [Fact]
    public async Task Forcing_the_deferred_check_immediate_cannot_skip_it()
    {
        var run = await db.SeedRunAsync(TenantA);
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, run.Tenant);
        var payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0003", run.Account(A1), run.Account(B1), 100);
        await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared", "Settled");
        var group = await Ledger.InsertGroupAsync(conn, run, payment, "Settlement");
        await Ledger.InsertLegAsync(conn, run, group, run.Account(A1), "Credit", 100);
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            Ledger.ExecAsync(conn, "SET CONSTRAINTS ALL IMMEDIATE"));
        Assert.Equal("RV002", error.SqlState);
    }

    [Fact]
    public async Task A_zero_value_leg_is_refused_on_insert()
    {
        var run = await db.SeedRunAsync(TenantA);
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, run.Tenant);
        var payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0004", run.Account(A1), run.Account(B1), 100);
        await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared", "Settled");
        var group = await Ledger.InsertGroupAsync(conn, run, payment, "Settlement");
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            Ledger.InsertLegAsync(conn, run, group, run.Account(A1), "Credit", 0));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task A_group_without_a_valid_payment_link_is_refused()
    {
        var run = await db.SeedRunAsync(TenantA);

        // Missing link: no such payment.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertGroupAsync(conn, run, Guid.NewGuid(), "Settlement"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        // Linked to a payment that is not Settled: a correct, balanced group alone is an orphan posting.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0005", run.Account(A1), run.Account(B1), 100);
            await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared");
            var group = await Ledger.InsertGroupAsync(conn, run, payment, "Settlement");
            await Ledger.InsertLegAsync(conn, run, group, run.Account(A1), "Credit", 100);
            await Ledger.InsertLegAsync(conn, run, group, run.Account(B1), "Debit", 100);
            var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal("RV003", error.SqlState);
        }

        // A Return group must name the payment's Settlement group (shape CHECK).
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0006", run.Account(A1), run.Account(B1), 100);
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertGroupAsync(conn, run, payment, "Return"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        await AssertNothingPersistedAsync(run);
    }

    [Fact]
    public async Task Settled_without_its_group_is_refused_and_a_later_group_cannot_rescue_it()
    {
        var run = await db.SeedRunAsync(TenantA);
        Guid payment;
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0007", run.Account(A1), run.Account(B1), 100);
            await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared");
            await tx.CommitAsync();
        }

        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            await Ledger.AdvanceAsync(conn, payment, "Settled");
            var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal("RV003", error.SqlState);
        }

        Assert.Equal("Cleared", await StatusAsync(run, payment));
    }

    [Fact]
    public async Task Only_legal_versioned_lifecycle_steps_are_accepted_and_identity_is_immutable()
    {
        var run = await db.SeedRunAsync(TenantA);
        Guid payment;
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0008", run.Account(A1), run.Account(B1), 100);
            await tx.CommitAsync();
        }

        async Task<string> Attempt(NpgsqlDataSource source, string sql)
        {
            await using var conn = await source.OpenConnectionAsync();
            await using var tx = await Ledger.BeginTenantAsync(conn, run.Tenant);
            var error = await Assert.ThrowsAsync<PostgresException>(() => Ledger.ExecAsync(conn, sql, ("p", payment)));
            return error.SqlState;
        }

        Assert.Equal("RV003", await Attempt(db.Writer, "UPDATE revenant.payment SET status = 'Settled', version = version + 1 WHERE id = @p"));
        Assert.Equal("RV003", await Attempt(db.Writer, "UPDATE revenant.payment SET status = 'Held', version = version + 1 WHERE id = @p"));
        Assert.Equal("RV003", await Attempt(db.Writer, "UPDATE revenant.payment SET status = 'Screened' WHERE id = @p"));
        Assert.Equal("RV003", await Attempt(db.Writer, "UPDATE revenant.payment SET status = 'Initiated', version = version + 1 WHERE id = @p"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            await Attempt(db.Writer, "UPDATE revenant.payment SET amount_minor = 1 WHERE id = @p"));
        Assert.Equal("RV001", await Attempt(db.Owner, "UPDATE revenant.payment SET amount_minor = 1, version = version + 1 WHERE id = @p"));

        // A new payment cannot be inserted already Settled (or past version 1).
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Ledger.ExecAsync(conn,
                "INSERT INTO revenant.payment (tenant_id, scenario_run_id, intent_id, source_account_id, dest_account_id, currency, amount_minor, status) " +
                "VALUES (@t, @r, 'FR-0009', @s, @d, 'USD', 100, 'Settled')",
                ("t", run.Tenant), ("r", run.RunId), ("s", run.Account(A1)), ("d", run.Account(B1))));
            Assert.Equal("RV003", error.SqlState);
        }

        // The legal path still works afterwards.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            await Ledger.AdvanceAsync(conn, payment, "Screened", "Held", "Rejected");
            await tx.CommitAsync();
        }

        Assert.Equal("Rejected", await StatusAsync(run, payment));
        Assert.Equal("RV003", await Attempt(db.Writer, "UPDATE revenant.payment SET status = 'Approved', version = version + 1 WHERE id = @p"));
    }

    [Fact]
    public async Task Cross_currency_payments_and_legs_cannot_be_expressed()
    {
        var run = await db.SeedRunAsync(TenantA);
        Guid eur;
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            eur = await Ledger.ScalarAsync<Guid>(conn,
                "INSERT INTO revenant.account (tenant_id, scenario_run_id, bank_id, local_account_id, currency, opening_balance_minor) " +
                "VALUES (@t, @r, @b, 'BANK-B/EUR-1', 'EUR', 1000) RETURNING id",
                ("t", run.Tenant), ("r", run.RunId), ("b", run.Banks["BANK-B"]));
            await tx.CommitAsync();
        }

        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertPaymentAsync(conn, run, "FR-FX", run.Account(A1), eur, 100));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0010", run.Account(A1), run.Account(B1), 100);
            await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared", "Settled");
            var group = await Ledger.InsertGroupAsync(conn, run, payment, "Settlement");
            var leg = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertLegAsync(conn, run, group, eur, "Debit", 100, "EUR"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, leg.SqlState);
        }

        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0011", run.Account(A1), run.Account(B1), 100);
            var group = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertGroupAsync(conn, run, payment, "Settlement", currency: "EUR"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, group.SqlState);
        }
    }

    [Fact]
    public async Task A_committed_group_is_sealed_and_posting_rows_are_append_only()
    {
        var run = await db.SeedRunAsync(TenantA);
        var (payment, group) = await Ledger.SettleAsync(db.Writer, run, "FR-0012", run.Account(A1), run.Account(B1), 500);

        // Even a balanced pair cannot be appended to a committed group.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertLegAsync(conn, run, group, run.Account(A1), "Credit", 1));
            Assert.Equal("RV002", error.SqlState);
        }

        // Writer: no privilege. Owner (migration superuser): the RV001 trigger, except that PostgreSQL refuses TRUNCATE
        // of an FK-referenced table (0A000) before any trigger runs; either way nothing is removed.
        var mutations = new (string Sql, string OwnerState)[]
        {
            ("UPDATE revenant.ledger_entry SET amount_minor = amount_minor + 1", "RV001"),
            ("DELETE FROM revenant.ledger_entry", "RV001"),
            ("TRUNCATE revenant.ledger_entry", "RV001"),
            ("UPDATE revenant.ledger_group SET purpose = 'Return'", "RV001"),
            ("DELETE FROM revenant.ledger_group", "RV001"),
            ("UPDATE revenant.account SET opening_balance_minor = 0", "RV001"),
            ("DELETE FROM revenant.account", "RV001"),
            ("UPDATE revenant.bank SET name = 'x'", "RV001"),
            ("DELETE FROM revenant.bank", "RV001"),
            ("DELETE FROM revenant.payment", "RV001"),
            ("TRUNCATE revenant.payment", PostgresErrorCodes.FeatureNotSupported),
        };
        foreach (var (sql, ownerState) in mutations)
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await FailureAsync(db.Writer, run.Tenant, sql));
            Assert.Equal(ownerState, await FailureAsync(db.Owner, null, sql));
        }

        var balances = await Ledger.PostedBalancesAsync(db.Writer, run);
        Assert.Equal(run.Opening[A1] - 500, balances[run.Account(A1)]);
        Assert.Equal("Settled", await StatusAsync(run, payment));
    }

    [Fact]
    public async Task A_return_is_a_new_mirror_group_that_restores_balances_and_keeps_the_original_rows()
    {
        var run = await db.SeedRunAsync(TenantA);
        var (src, dst) = (run.Account(A1), run.Account(B1));
        var (payment, settlement) = await Ledger.SettleAsync(db.Writer, run, "FR-0013", src, dst, 777);
        var originalRows = await EntryDigestAsync(run, settlement);

        // A return group without moving the payment to Returned is an orphan posting.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var ret = await Ledger.InsertGroupAsync(conn, run, payment, "Return", settlement);
            await Ledger.InsertLegAsync(conn, run, ret, src, "Debit", 777);
            await Ledger.InsertLegAsync(conn, run, ret, dst, "Credit", 777);
            Assert.Equal("RV003", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        }

        // Returned after settlement without the compensating group is refused.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            await Ledger.AdvanceAsync(conn, payment, "Returned");
            Assert.Equal("RV003", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        }

        // A return that repeats the settlement direction is refused.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            await Ledger.AdvanceAsync(conn, payment, "Returned");
            var ret = await Ledger.InsertGroupAsync(conn, run, payment, "Return", settlement);
            await Ledger.InsertLegAsync(conn, run, ret, src, "Credit", 777);
            await Ledger.InsertLegAsync(conn, run, ret, dst, "Debit", 777);
            Assert.Equal("RV002", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        }

        // The correct compensation commits.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            await Ledger.AdvanceAsync(conn, payment, "Returned");
            var ret = await Ledger.InsertGroupAsync(conn, run, payment, "Return", settlement);
            await Ledger.InsertLegAsync(conn, run, ret, src, "Debit", 777);
            await Ledger.InsertLegAsync(conn, run, ret, dst, "Credit", 777);
            await tx.CommitAsync();
        }

        Assert.Equal("Returned", await StatusAsync(run, payment));
        Assert.Equal(originalRows, await EntryDigestAsync(run, settlement));
        var balances = await Ledger.PostedBalancesAsync(db.Writer, run);
        Assert.Equal(run.Opening[A1], balances[src]);
        Assert.Equal(run.Opening[B1], balances[dst]);

        // A second return of the same settlement is refused (and Returned is terminal anyway).
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertGroupAsync(conn, run, payment, "Return", settlement));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task Two_concurrent_settlements_of_one_payment_commit_exactly_once()
    {
        var run = await db.SeedRunAsync(TenantA);
        var (src, dst) = (run.Account(A1), run.Account(B1));
        Guid payment;
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, run.Tenant))
        {
            payment = await Ledger.InsertPaymentAsync(conn, run, "FR-0014", src, dst, 250);
            await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared");
            await tx.CommitAsync();
        }

        await using var first = await db.Writer.OpenConnectionAsync();
        await using var second = await db.Writer.OpenConnectionAsync();
        await using var tx1 = await Ledger.BeginTenantAsync(first, run.Tenant);
        await using var tx2 = await Ledger.BeginTenantAsync(second, run.Tenant);
        await Ledger.AdvanceAsync(first, payment, "Settled");
        var group = await Ledger.InsertGroupAsync(first, run, payment, "Settlement");
        await Ledger.InsertLegAsync(first, run, group, src, "Credit", 250);
        await Ledger.InsertLegAsync(first, run, group, dst, "Debit", 250);

        var racer = Ledger.AdvanceAsync(second, payment, "Settled");
        await Task.Delay(300);
        Assert.False(racer.IsCompleted, "the second settlement must wait on the payment row lock");
        await tx1.CommitAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => racer);
        Assert.Equal("RV003", error.SqlState);
        await tx2.RollbackAsync();

        var balances = await Ledger.PostedBalancesAsync(db.Writer, run);
        Assert.Equal(run.Opening[A1] - 250, balances[src]);
        Assert.Equal(run.Opening[B1] + 250, balances[dst]);
        Assert.Equal(1L, await CountAsync(run, "SELECT count(*) FROM revenant.ledger_group WHERE payment_id = @p", payment));
    }

    [Fact]
    public async Task Concurrent_settlements_out_of_one_hot_account_all_post_and_conserve_the_total()
    {
        var run = await db.SeedRunAsync(TenantA);
        var hot = run.Account(A1);
        var targets = run.Accounts.Where(a => a.Key != A1).Select(a => a.Value).ToArray();
        var amounts = Enumerable.Range(1, 24).Select(i => (long)(i * 101)).ToArray();

        await Task.WhenAll(amounts.Select((amount, i) =>
            Ledger.SettleAsync(db.Writer, run, $"HOT-{i:D3}", hot, targets[i % targets.Length], amount)));

        var balances = await Ledger.PostedBalancesAsync(db.Writer, run);
        Assert.Equal(run.Opening[A1] - amounts.Sum(), balances[hot]);
        Assert.Equal(run.TotalOpening, balances.Values.Sum());
        Assert.Equal(48L, await CountAsync(run,
            "SELECT count(*) FROM revenant.ledger_entry e JOIN revenant.ledger_group g ON g.id = e.group_id " +
            "JOIN revenant.payment p ON p.id = g.payment_id WHERE p.intent_id LIKE 'HOT-%' AND e.scenario_run_id = @p", run.RunId));
    }

    [Fact]
    public async Task Equal_seed_runs_get_separate_account_namespaces_and_run_scoped_intent_keys()
    {
        var runA = await db.SeedRunAsync(TenantA, seed: 42);
        var runB = await db.SeedRunAsync(TenantA, seed: 42);

        Assert.Equal(runA.Accounts.Keys.Order(), runB.Accounts.Keys.Order());
        Assert.Empty(runA.Accounts.Values.Intersect(runB.Accounts.Values));
        Assert.Equal(runA.Opening, runB.Opening);

        await Ledger.SettleAsync(db.Writer, runA, "FR-0001", runA.Account(A1), runA.Account(B1), 1_000);
        await Ledger.SettleAsync(db.Writer, runB, "FR-0001", runB.Account(A2), runB.Account(C1), 2_000);

        // Same intent id inside one run is a duplicate.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, runA.Tenant))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertPaymentAsync(conn, runA, "FR-0001", runA.Account(A1), runA.Account(B1), 1_000));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }

        // A payment or leg in run A cannot touch run B's accounts.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, runA.Tenant))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertPaymentAsync(conn, runA, "FR-0099", runA.Account(A1), runB.Account(B1), 10));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, runA.Tenant))
        {
            var payment = await Ledger.InsertPaymentAsync(conn, runA, "FR-0098", runA.Account(A1), runA.Account(B1), 10);
            await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared", "Settled");
            var group = await Ledger.InsertGroupAsync(conn, runA, payment, "Settlement");
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertLegAsync(conn, runA, group, runB.Account(A1), "Credit", 10));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        var balancesA = await Ledger.PostedBalancesAsync(db.Writer, runA);
        var balancesB = await Ledger.PostedBalancesAsync(db.Writer, runB);
        Assert.Equal(runA.Opening[A1] - 1_000, balancesA[runA.Account(A1)]);
        Assert.Equal(runB.Opening[A1], balancesB[runB.Account(A1)]);
        Assert.Equal(runB.Opening[A2] - 2_000, balancesB[runB.Account(A2)]);
        Assert.Equal(runA.TotalOpening, balancesA.Values.Sum());
        Assert.Equal(runB.TotalOpening, balancesB.Values.Sum());
    }

    [Fact]
    public async Task Rows_are_visible_and_writable_only_in_the_transaction_local_tenant_context()
    {
        var run = await db.SeedRunAsync(TenantA);
        await Ledger.SettleAsync(db.Writer, run, "FR-0001", run.Account(A1), run.Account(B1), 100);
        const string count = "SELECT count(*) FROM revenant.account WHERE scenario_run_id = @p";

        Assert.Equal(18L, await CountAsync(run, count, run.RunId));
        Assert.Equal(0L, await CountAsync(run with { Tenant = TenantB }, count, run.RunId));

        // The setting is transaction-local: the same pooled connection sees nothing in its next transaction.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        {
            await using (var tx = await Ledger.BeginTenantAsync(conn, TenantA))
            {
                Assert.Equal(18L, await Ledger.ScalarAsync<long>(conn, count, ("p", run.RunId)));
                await tx.CommitAsync();
            }

            await using (var tx = await Ledger.BeginTenantAsync(conn, null))
            {
                Assert.Equal(0L, await Ledger.ScalarAsync<long>(conn, count, ("p", run.RunId)));
                var error = await Assert.ThrowsAsync<PostgresException>(() =>
                    Ledger.InsertPaymentAsync(conn, run, "FR-NOCTX", run.Account(A1), run.Account(B1), 1));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }

        // Tenant B cannot write tenant A rows by naming them, nor reference tenant A accounts from its own rows.
        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, TenantB))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertPaymentAsync(conn, run, "FR-CROSS", run.Account(A1), run.Account(B1), 1));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await using (var conn = await db.Writer.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, TenantB))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Ledger.InsertPaymentAsync(conn, run with { Tenant = TenantB }, "FR-CROSS", run.Account(A1), run.Account(B1), 1));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
    }

    [Fact]
    public async Task The_replay_reader_can_read_its_tenant_but_cannot_write_any_ledger_table()
    {
        var run = await db.SeedRunAsync(TenantA);
        var (payment, group) = await Ledger.SettleAsync(db.Writer, run, "FR-0001", run.Account(A1), run.Account(B1), 100);

        await using (var conn = await db.Replay.OpenConnectionAsync())
        await using (var tx = await Ledger.BeginTenantAsync(conn, TenantA))
        {
            Assert.Equal(2L, await Ledger.ScalarAsync<long>(conn,
                "SELECT count(*) FROM revenant.ledger_entry WHERE group_id = @g", ("g", group)));
        }

        var denied = new[]
        {
            $"INSERT INTO revenant.ledger_group (tenant_id, scenario_run_id, payment_id, currency, purpose) VALUES ('{TenantA}', '{run.RunId}', '{payment}', 'USD', 'Settlement')",
            $"INSERT INTO revenant.ledger_entry (group_id, tenant_id, scenario_run_id, account_id, currency, direction, amount_minor) VALUES ('{group}', '{TenantA}', '{run.RunId}', '{run.Account(A1)}', 'USD', 'Debit', 1)",
            $"INSERT INTO revenant.payment (tenant_id, scenario_run_id, intent_id, source_account_id, dest_account_id, currency, amount_minor, status) VALUES ('{TenantA}', '{run.RunId}', 'R-1', '{run.Account(A1)}', '{run.Account(B1)}', 'USD', 1, 'Initiated')",
            $"INSERT INTO revenant.account (tenant_id, scenario_run_id, bank_id, local_account_id, currency, opening_balance_minor) VALUES ('{TenantA}', '{run.RunId}', '{run.Banks["BANK-A"]}', 'X', 'USD', 1)",
            "UPDATE revenant.payment SET status = 'Returned', version = version + 1",
            "DELETE FROM revenant.ledger_entry",
            "TRUNCATE revenant.ledger_group",
        };
        foreach (var sql in denied)
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await FailureAsync(db.Replay, TenantA, sql));
        }
    }

    [Fact]
    public async Task Runtime_roles_are_not_owners_superusers_or_rls_bypassers_and_rls_is_forced()
    {
        await using var conn = await db.Owner.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT rolname, rolsuper, rolbypassrls, rolcanlogin FROM pg_roles " +
                "WHERE rolname IN ('revenant_ledger_writer', 'revenant_replay_reader') ORDER BY rolname";
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                Assert.False(reader.GetBoolean(1));
                Assert.False(reader.GetBoolean(2));
                Assert.False(reader.GetBoolean(3));
            }

            Assert.Equal(2, rows);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.relname, pg_get_userbyid(c.relowner), c.relrowsecurity, c.relforcerowsecurity FROM pg_class c " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'revenant' AND c.relkind = 'r' " +
                "AND c.relname IN ('bank', 'account', 'payment', 'ledger_group', 'ledger_entry')";
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                Assert.DoesNotContain(reader.GetString(1), new[] { "revenant_ledger_writer", "revenant_replay_reader" });
                Assert.True(reader.GetBoolean(2), $"{reader.GetString(0)} RLS enabled");
                Assert.True(reader.GetBoolean(3), $"{reader.GetString(0)} RLS forced");
            }

            Assert.Equal(5, rows);
        }
    }

    /// <summary>
    /// Property: for random same-currency transfers among the six banks' accounts, where some transfers carry a
    /// random leg imbalance, PostgreSQL commits exactly the balanced ones, every account's posted balance equals an
    /// independent reference fold, and the six-bank total stays equal to the opening total.
    /// </summary>
    [Fact]
    public void Balanced_groups_conserve_the_six_bank_total_and_every_imbalance_is_refused()
    {
        var accounts = FridayRingScenario.Create(42, ScenarioSchemas.FridayRingVersion, new ScenarioRunId(Guid.NewGuid()))
            .Value.Manifest.Accounts.Select(a => a.AccountId).ToArray();
        var transfer =
            from src in Gen.Choose(0, accounts.Length - 1)
            from offset in Gen.Choose(1, accounts.Length - 1)
            from amount in Gen.Choose(1, 50_000)
            from skew in Gen.Frequency((3, Gen.Constant(0)), (1, Gen.Choose(-500, 500)))
            from creditSide in Gen.Elements(false, true)
            select (Src: src, Dst: (src + offset) % accounts.Length, Amount: (long)amount, Skew: (long)skew, CreditSide: creditSide);

        var batches =
            from n in Gen.Choose(1, 8)
            from list in Gen.ArrayOf(transfer, n)
            select list;

        Prop.ForAll(batches.ToArbitrary(), transfers =>
        {
            var run = db.SeedRunAsync(TenantA).GetAwaiter().GetResult();
            var expected = run.Accounts.ToDictionary(a => a.Value, a => run.Opening[a.Key]);
            var index = 0;
            foreach (var (srcIdx, dstIdx, amount, skew, creditSide) in transfers)
            {
                // The skew lands on either leg, so neither "debits = credits" nor "total = payment amount" alone
                // is enough to reject it.
                var (src, dst) = (run.Account(accounts[srcIdx]), run.Account(accounts[dstIdx]));
                var (credit, debit) = creditSide ? (amount + skew, amount) : (amount, amount + skew);
                var committed = TrySettle(run, $"P-{index++:D3}", src, dst, amount, credit, debit).GetAwaiter().GetResult();
                if (committed != (skew == 0))
                {
                    return false;
                }

                if (committed)
                {
                    expected[src] -= amount;
                    expected[dst] += amount;
                }
            }

            var actual = Ledger.PostedBalancesAsync(db.Writer, run).GetAwaiter().GetResult();
            return actual.Values.Sum() == run.TotalOpening && expected.All(e => actual[e.Key] == e.Value);
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(25));
    }

    private async Task<bool> TrySettle(SeededRun run, string intent, Guid src, Guid dst, long amount, long credit, long debit)
    {
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, run.Tenant);
        var payment = await Ledger.InsertPaymentAsync(conn, run, intent, src, dst, amount);
        await Ledger.AdvanceAsync(conn, payment, "Screened", "Cleared", "Settled");
        var group = await Ledger.InsertGroupAsync(conn, run, payment, "Settlement");
        if (credit > 0)
        {
            await Ledger.InsertLegAsync(conn, run, group, src, "Credit", credit);
        }

        if (debit > 0)
        {
            await Ledger.InsertLegAsync(conn, run, group, dst, "Debit", debit);
        }

        try
        {
            await tx.CommitAsync();
            return true;
        }
        catch (PostgresException e) when (e.SqlState == "RV002")
        {
            return false;
        }
    }

    private async Task<string> FailureAsync(NpgsqlDataSource source, string? tenant, string sql)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, tenant);
        var error = await Assert.ThrowsAsync<PostgresException>(() => Ledger.ExecAsync(conn, sql));
        return error.SqlState;
    }

    private async Task<long> CountAsync(SeededRun run, string sql, Guid parameter)
    {
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, run.Tenant);
        return await Ledger.ScalarAsync<long>(conn, sql, ("p", parameter));
    }

    private async Task<string> StatusAsync(SeededRun run, Guid payment)
    {
        await using var conn = await db.Writer.OpenConnectionAsync();
        await using var tx = await Ledger.BeginTenantAsync(conn, run.Tenant);
        return await Ledger.ScalarAsync<string>(conn, "SELECT status FROM revenant.payment WHERE id = @p", ("p", payment));
    }

    private async Task<string> EntryDigestAsync(SeededRun run, Guid group)
    {
        await using var conn = await db.Owner.OpenConnectionAsync();
        return await Ledger.ScalarAsync<string>(conn,
            "SELECT string_agg(row_to_json(e)::text, '|' ORDER BY e.id) FROM revenant.ledger_entry e WHERE e.group_id = @g",
            ("g", group));
    }

    private async Task AssertNothingPersistedAsync(SeededRun run)
    {
        await using var conn = await db.Owner.OpenConnectionAsync();
        Assert.Equal(0L, await Ledger.ScalarAsync<long>(conn,
            "SELECT (SELECT count(*) FROM revenant.payment WHERE scenario_run_id = @r) + " +
            "(SELECT count(*) FROM revenant.ledger_group WHERE scenario_run_id = @r) + " +
            "(SELECT count(*) FROM revenant.ledger_entry WHERE scenario_run_id = @r)", ("r", run.RunId)));
    }
}
