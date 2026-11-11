using System.Collections.Immutable;
using Marrow.Infrastructure.Messaging;
using Marrow.Kernel.Messaging;
using Marrow.Kernel.Security;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Payments;
using Revenant.Scenario;
using Xunit;

namespace Revenant.Tests.Payments;

/// <summary>One PostgreSQL container shared by the Spec 07 payment test classes (they run one after another).</summary>
[CollectionDefinition(Name)]
public sealed class PaymentCollection : ICollectionFixture<Revenant.Tests.Ledger.LedgerDatabase>
{
    public const string Name = "revenant-ledger-db";
}

internal static class PaymentKit
{
    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";
    public const string Operator = "staff:operator-1";

    public static PaymentLedgerStore Store(NpgsqlDataSource writer, Func<NpgsqlTransaction, IOutboxWriter>? outbox = null) =>
        new(writer, outbox ?? (tx => new PostgresOutboxWriter(tx, new MessagingTables("revenant"))), TimeProvider.System);

    public static async Task<ScenarioRunId> OpenAsync(
        PaymentLedgerStore store, string tenant = TenantA, string owner = Operator, long seed = 42, ScenarioRunId? id = null)
    {
        var run = id ?? new ScenarioRunId(Guid.NewGuid());
        var receipt = await store.OpenRunAsync(tenant, owner, "operator", seed, run, CancellationToken.None);
        return receipt.TryGetValue(out _) ? run : throw new InvalidOperationException($"OpenRun failed: {receipt.Error}");
    }

    /// <summary>The generated Friday Ring intents of <paramref name="run"/>: real ordered facts, valid accounts, USD.</summary>
    public static ImmutableArray<PaymentIntent> Intents(ScenarioRunId run, long seed = 42) =>
        FridayRingScenario.Create(seed, ScenarioSchemas.FridayRingVersion, run).Value.Intents;

    public static Task<T> Scalar<T>(NpgsqlDataSource source, string sql, params (string Name, object Value)[] args) => ScalarAsync<T>(source, sql, args);

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql, (string Name, object Value)[] args)
    {
        await using var cmd = source.CreateCommand(sql);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)result;
    }

    public static async Task<long> Count(NpgsqlDataSource owner, string table, Guid run, string runColumn = "scenario_run_id") =>
        await ScalarAsync<long>(owner, $"SELECT count(*) FROM revenant.{table} WHERE {runColumn} = @r", [("r", run)]);

    public const string Approver = "staff:approver-1";
    public const string Supervisor = "staff:supervisor-1";

    /// <summary>
    /// Releases a held payment the only way the ledger allows since task 7.4: an MFA-verified approval on its review case, plus a
    /// second, distinct MFA-verified supervisor when the case requires two.
    /// </summary>
    public static async Task ReleaseThroughCaseAsync(PaymentLedgerStore store, string tenant, ScenarioRunId run, string intent)
    {
        var view = await store.GetCaseAsync(tenant, run, intent, CancellationToken.None)
            ?? throw new InvalidOperationException($"{intent} has no review case");
        foreach (var actor in new[] { new CaseActor(Approver, true, false, true), new CaseActor(Supervisor, true, true, true) })
        {
            if (view.Status == CaseStatus.Released)
            {
                break;
            }
            var receipt = await store.CaseActionAsync(tenant, actor,
                new CaseCommand(run, intent, CaseAction.Approve, view.Version, $"approve-{intent}-{view.Version}", null), "c", CancellationToken.None);
            view = receipt.IsSuccess ? receipt.Value.Case : throw new InvalidOperationException($"approve {intent} failed: {receipt.Error}");
        }
    }

    /// <summary>Outbox rows whose aggregate is any payment of the run.</summary>
    public static async Task<long> OutboxRows(NpgsqlDataSource owner, Guid run) =>
        await ScalarAsync<long>(owner,
            "SELECT count(*) FROM revenant.outbox o JOIN revenant.payment p ON o.aggregate_id = 'payment:' || replace(p.id::text, '-', '') WHERE p.scenario_run_id = @r",
            [("r", run)]);
}

/// <summary>A verified-staff stand-in for tests of the authorizer: exactly the facts the real identity layer supplies.</summary>
internal sealed record TestActor(
    string SubjectId, PrincipalKind PrincipalKind, string? TenantId, IReadOnlyCollection<string> Roles, bool IsAuthenticated = true) : IActorContext
{
    public IReadOnlyCollection<string> Claims { get; } = ["mfa"];

    /// <summary>A tenant-bearing staff actor (tenant A unless stated): the identity layer attaches the server-side tenant.</summary>
    public static TestActor Staff(string subject, string role = "operator", string? tenant = PaymentKit.TenantA) =>
        new(subject, PrincipalKind.Staff, tenant, [role]);

    /// <summary>Staff with no active tenant membership (never granted, or revoked).</summary>
    public static TestActor Tenantless(string subject, string role = "operator") => new(subject, PrincipalKind.Staff, null, [role]);
}

/// <summary>A real <see cref="IOutboxWriter"/> that fails, to prove the business write rolls back with it.</summary>
internal sealed class FailingOutboxWriter : IOutboxWriter
{
    public Task EnqueueAsync(OutboxMessage message, CancellationToken ct) =>
        throw new InvalidOperationException("outbox write failed");
}
