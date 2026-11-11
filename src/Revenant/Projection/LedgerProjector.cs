using System.Data;
using System.Security.Cryptography;
using Marrow.Kernel.Results;
using Marrow.Timeline;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Scenario;
using Revenant.Timeline;

namespace Revenant.Projection;

public enum ProjectionErrorCode
{
    RunNotFound,
    NotDurableV2,
    ManifestMismatch,
    FactHashMismatch,
    Reducer,
    Timeline,
    FactsAfterVerification,
    NotCaughtUp,

    /// <summary>The live run is still open to new financial facts; close it (<c>PaymentLedgerStore.CloseRunAsync</c>) before verifying.</summary>
    RunNotClosed,
}

public sealed record ProjectionError(ProjectionErrorCode Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>Projection progress of one run: the V2 history, how far it is projected and how many facts are committed.</summary>
public sealed record ProjectionStatus(
    ScenarioRunId RunId, TimelineId History, long ProjectedPosition, long CommittedFacts, HistoryStatus Status, string FinalStateHash, int Appended);

/// <summary>One difference between the pure V2 source and the live ledger.</summary>
public sealed record ReconciliationMismatch(string Subject, string Source, string Live);

/// <summary>
/// Outcome of <see cref="LedgerProjector.VerifySourceAsync"/>. <see cref="Projection"/>.Status is Verified only when the projection
/// had caught up with every committed fact and <see cref="Mismatches"/> is empty.
/// </summary>
public sealed record SourceVerification(
    ProjectionStatus Projection, bool Reconciled, IReadOnlyList<ReconciliationMismatch> Mismatches, LedgerFinanceState SourceState);

/// <summary>
/// Spec 07 task 7.5: the restartable, idempotent projection of a durable run's ordered canonical facts into its V2 Marrow Timeline
/// source history (ADR-P1-17 decision 3), and the live-source verification.
///
/// Fact <c>k</c> becomes position <c>k</c> with operation key <c>revenant-ledger-step/{run}/{k}</c>; the history itself is created
/// with operation key <c>revenant-ledger-source/{run}</c>. Progress is the history's own last position (no second cursor), so a
/// restart after a crash between the live commit and the projection, or a retry of an append that already committed, re-applies
/// nothing. Facts are read in <c>fact_seq</c> order, which is the live commit order, and each payload is checked against its stored
/// SHA-256 before it is appended. The projector reads the live tables (it needs only SELECT: in deployment it runs as a
/// <c>revenant_replay_reader</c> + <c>marrow_timeline_runtime</c> login) and writes only Timeline records through the shared engine;
/// it has no ledger-writing capability. A tenant is always selected explicitly by the caller, never from a request.
/// </summary>
public sealed class LedgerProjector(NpgsqlDataSource reader, ITimelineEngine<LedgerFinanceState, FinanceFact, LedgerFinanceContext> engine)
{
    public static string SourceOperationKey(ScenarioRunId run) => $"revenant-ledger-source/{run.Value:N}";

    public static string StepOperationKey(ScenarioRunId run, long position) => $"revenant-ledger-step/{run.Value:N}/{position}";

    internal sealed record RunRow(string TenantId, string Owner, long Seed, string ScenarioVersion, string ManifestHash, string FinanceVersion);

    /// <summary>Runs of <paramref name="tenantId"/> recorded under the V2 finance version.</summary>
    public async Task<IReadOnlyList<ScenarioRunId>> ListRunsAsync(string tenantId, CancellationToken ct)
    {
        await using var cmd = reader.CreateCommand(
            "SELECT id FROM revenant.scenario_run WHERE tenant_id = @tenant AND finance_version = @v ORDER BY created_at, id");
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("v", FinanceLedgerVersion.ReducerVersion);
        var runs = new List<ScenarioRunId>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            runs.Add(new ScenarioRunId(r.GetGuid(0)));
        }
        return runs;
    }

    /// <summary>
    /// Appends every committed fact after the history's last position (up to <paramref name="upTo"/> when given). Safe to call any
    /// number of times, concurrently, and after any crash.
    /// </summary>
    public async Task<Result<ProjectionStatus, ProjectionError>> CatchUpAsync(
        string tenantId, ScenarioRunId runId, CancellationToken ct, long? upTo = null)
    {
        var opened = await OpenAsync(tenantId, runId, ct);
        if (!opened.TryGetValue(out var source))
        {
            return Result<ProjectionStatus, ProjectionError>.Failure(opened.Error);
        }
        for (var attempt = 0; ; attempt++)
        {
            var result = await AppendPendingAsync(source, upTo, ct);
            if (result.IsSuccess || result.Error.Code != ProjectionErrorCode.Timeline || attempt >= 2
                || !result.Error.Message.StartsWith(nameof(TimelineErrorCode.Conflict), StringComparison.Ordinal))
            {
                return result;
            }
            // Another projector appended the same position first; re-read the history and continue from there.
        }
    }

    /// <summary>
    /// Catches the run's history up with every committed fact, then compares the pure source state with one consistent snapshot of
    /// the live ledger: every payment's status and full status trail (hence version), its reservation and hold, its review case
    /// status, its settlement and return postings, and every account's posted and reserved balance. Only when the snapshot shows no
    /// fact beyond the projected position and nothing differs is the history completed to Verified by the engine's own replay.
    /// </summary>
    public async Task<Result<SourceVerification, ProjectionError>> VerifySourceAsync(string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        var opened = await OpenAsync(tenantId, runId, ct);
        if (!opened.TryGetValue(out var source))
        {
            return Result<SourceVerification, ProjectionError>.Failure(opened.Error);
        }
        var caught = await AppendPendingAsync(source, null, ct);
        if (!caught.TryGetValue(out var status))
        {
            return Result<SourceVerification, ProjectionError>.Failure(caught.Error);
        }
        var replay = await engine.ReplayAsync(source.History, ct);
        if (!replay.TryGetValue(out var replayed))
        {
            return TimelineFail<SourceVerification>(replay.Error);
        }
        var live = await ReadLiveSnapshotAsync(reader, source.TenantId, runId, ct);
        if (live.ClosedAt is null)
        {
            return Result<SourceVerification, ProjectionError>.Failure(new ProjectionError(ProjectionErrorCode.RunNotClosed,
                "The live run is open to new financial facts; close it before verifying so the verified source is its final set of facts."));
        }
        if (live.CommittedFacts != replayed.FinalPosition || live.ClosedAt != live.CommittedFacts)
        {
            return Result<SourceVerification, ProjectionError>.Failure(new ProjectionError(ProjectionErrorCode.NotCaughtUp,
                $"The live run has {live.CommittedFacts} facts and the source history {replayed.FinalPosition}; retry after catch-up."));
        }
        var mismatches = Reconcile(replayed.FinalState, live);
        if (mismatches.Count > 0 || replayed.Status == HistoryStatus.Verified)
        {
            return Result<SourceVerification, ProjectionError>.Success(new SourceVerification(
                status with { Status = replayed.Status, FinalStateHash = replayed.FinalStateHash }, mismatches.Count == 0, mismatches, replayed.FinalState));
        }
        var completed = await engine.CompleteAsync(source.History, replayed.FinalPosition, ct);
        if (!completed.TryGetValue(out var receipt))
        {
            return TimelineFail<SourceVerification>(completed.Error);
        }
        return Result<SourceVerification, ProjectionError>.Success(new SourceVerification(
            status with { Status = receipt.Status, FinalStateHash = receipt.FinalStateHash, ProjectedPosition = receipt.FinalPosition },
            true, [], replayed.FinalState));
    }

    // ---------------------------------------------------------------------------------------------------------

    private sealed record OpenedSource(ScenarioRunId RunId, string TenantId, TimelineId History, LedgerFinanceContext Context);

    private async Task<Result<OpenedSource, ProjectionError>> OpenAsync(string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        var row = await ReadRunAsync(reader, runId, ct);
        if (row is null || !string.Equals(row.TenantId, tenantId, StringComparison.Ordinal))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.RunNotFound, "The run does not exist in this tenant.");
        }
        if (!string.Equals(row.FinanceVersion, FinanceLedgerVersion.ReducerVersion, StringComparison.Ordinal))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.NotDurableV2, $"The run is recorded under '{row.FinanceVersion}'.");
        }
        var created = FridayRingScenario.Create(row.Seed, row.ScenarioVersion, runId);
        if (!created.TryGetValue(out var run) || !string.Equals(run.Manifest.ContentHash, row.ManifestHash, StringComparison.Ordinal))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.ManifestMismatch, "The run's stored manifest hash does not match its seed and scenario version.");
        }
        var pinned = LedgerFinanceContext.Pin(run.Manifest, PolicyCatalog.RefOf(LedgerFinanceContext.LivePolicy));
        if (!pinned.TryGetValue(out var context))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.Reducer, pinned.Error.ToString());
        }
        var initial = FinanceLedgerReducer.Initial(run, context);
        if (!initial.TryGetValue(out var state))
        {
            return Fail<OpenedSource>(ProjectionErrorCode.Reducer, initial.Error.ToString());
        }
        var history = await engine.CreateSourceAsync(
            new CreateSourceRequest<LedgerFinanceState, LedgerFinanceContext>(
                row.Owner, FinanceLedgerTimelineVersions.V2, state, context, FinanceLedgerVersion.ReducerVersion, row.ManifestHash),
            SourceOperationKey(runId), ct);
        return history.TryGetValue(out var id)
            ? Result<OpenedSource, ProjectionError>.Success(new OpenedSource(runId, row.TenantId, id, context))
            : TimelineFail<OpenedSource>(history.Error);
    }

    private async Task<Result<ProjectionStatus, ProjectionError>> AppendPendingAsync(OpenedSource source, long? upTo, CancellationToken ct)
    {
        var replay = await engine.ReplayAsync(source.History, ct);
        if (!replay.TryGetValue(out var current))
        {
            return TimelineFail<ProjectionStatus>(replay.Error);
        }
        var (committed, facts) = await ReadFactsAsync(reader, source.TenantId, source.RunId, current.FinalPosition, upTo, ct);
        if (facts.Count > 0 && current.Status != HistoryStatus.Recording)
        {
            return Fail<ProjectionStatus>(ProjectionErrorCode.FactsAfterVerification,
                $"The source history is {current.Status} at position {current.FinalPosition}, and fact {facts[0].Sequence} was committed after it.");
        }
        var position = current.FinalPosition;
        var state = current.FinalState;
        var stateHash = current.FinalStateHash;
        foreach (var fact in facts)
        {
            if (fact.Sequence != position + 1)
            {
                return Fail<ProjectionStatus>(ProjectionErrorCode.Reducer, $"Fact {fact.Sequence} does not follow position {position}.");
            }
            // Pre-validate with the same pure reducer the engine runs, so a rejected fact is a typed error and never reaches the journal.
            var next = FinanceLedgerReducer.Apply(state, fact, source.Context);
            if (!next.TryGetValue(out var after))
            {
                return Fail<ProjectionStatus>(ProjectionErrorCode.Reducer, $"Fact {fact.Sequence}: {next.Error}");
            }
            var step = new StepRecord<FinanceFact>(fact.Sequence,
                [new OrderedInput<FinanceFact>(FinanceLedgerTransition.InputId(source.RunId, fact.Sequence), 0, fact)]);
            var appended = await engine.AppendAsync(source.History, position, step, StepOperationKey(source.RunId, fact.Sequence), ct);
            if (!appended.TryGetValue(out var receipt))
            {
                return TimelineFail<ProjectionStatus>(appended.Error);
            }
            (position, state, stateHash) = (receipt.Position, after, receipt.StateHash);
        }
        return Result<ProjectionStatus, ProjectionError>.Success(
            new ProjectionStatus(source.RunId, source.History, position, committed, current.Status, stateHash, facts.Count));
    }

    internal static async Task<RunRow?> ReadRunAsync(NpgsqlDataSource reader, ScenarioRunId runId, CancellationToken ct)
    {
        await using var cmd = reader.CreateCommand(
            "SELECT tenant_id, owner_subject_id, seed, scenario_version, manifest_hash, finance_version FROM revenant.scenario_run WHERE id = @id");
        cmd.Parameters.AddWithValue("id", runId.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new RunRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5))
            : null;
    }

    internal static async Task<(long Committed, List<FinanceFact> Facts)> ReadFactsAsync(
        NpgsqlDataSource reader, string tenantId, ScenarioRunId runId, long after, long? upTo, CancellationToken ct)
    {
        await using var connection = await reader.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        long committed;
        await using (var cmd = new NpgsqlCommand("SELECT last_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @run", connection, tx))
        {
            cmd.Parameters.AddWithValue("run", runId.Value);
            committed = await cmd.ExecuteScalarAsync(ct) is long value ? value : 0;
        }
        var facts = new List<FinanceFact>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT fact_seq, kind, schema_version, canonical, canonical_sha256, evidence_schema, evidence_sha256
              FROM revenant.finance_fact
             WHERE scenario_run_id = @run AND fact_seq > @after AND fact_seq <= @upto
             ORDER BY fact_seq
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue("run", runId.Value);
            cmd.Parameters.AddWithValue("after", after);
            cmd.Parameters.AddWithValue("upto", upTo ?? long.MaxValue);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var payload = r.GetFieldValue<byte[]>(3);
                if (!SHA256.HashData(payload).AsSpan().SequenceEqual(r.GetFieldValue<byte[]>(4)))
                {
                    throw new ProjectionIntegrityException($"Fact {r.GetInt64(0)} of run {runId} does not match its stored SHA-256.");
                }
                var evidence = r.IsDBNull(5) ? null : new FactEvidenceRef(r.GetString(5), r.GetFieldValue<byte[]>(6));
                facts.Add(new FinanceFact(Enum.Parse<FinanceFactKind>(r.GetString(1)), r.GetInt64(0), r.GetString(2), payload, evidence));
            }
        }
        await tx.CommitAsync(ct);
        return (committed, facts);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Live snapshot and reconciliation
    // ---------------------------------------------------------------------------------------------------------

    internal sealed record LivePayment(string IntentId, PaymentStatus Status, long Version, List<PaymentStatus> Trail, string? HoldStatus,
        long HoldAmount, CaseStatus? Case, bool Settlement, bool Return);

    internal sealed record LiveSnapshot(long CommittedFacts, long? ClosedAt, Dictionary<string, (long Posted, long Reserved)> Accounts, Dictionary<string, LivePayment> Payments);

    internal static async Task<LiveSnapshot> ReadLiveSnapshotAsync(NpgsqlDataSource reader, string tenantId, ScenarioRunId runId, CancellationToken ct)
    {
        await using var connection = await reader.OpenConnectionAsync(ct);
        // One snapshot for the cursor and every live table, so the comparison is against one committed point of the run.
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await SetTenantAsync(connection, tx, tenantId, ct);
        var run = runId.Value;
        long committed;
        long? closedAt = null;
        await using (var cmd = new NpgsqlCommand("SELECT last_fact_seq, closed_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @run", connection, tx))
        {
            cmd.Parameters.AddWithValue("run", run);
            await using var cursor = await cmd.ExecuteReaderAsync(ct);
            committed = 0;
            if (await cursor.ReadAsync(ct))
            {
                (committed, closedAt) = (cursor.GetInt64(0), cursor.IsDBNull(1) ? null : cursor.GetInt64(1));
            }
        }
        var accounts = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            "SELECT local_account_id, posted_minor, reserved_minor FROM revenant.account_available_balance WHERE scenario_run_id = @run", connection, tx))
        {
            cmd.Parameters.AddWithValue("run", run);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                accounts[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
            }
        }
        var payments = new Dictionary<string, LivePayment>(StringComparer.Ordinal);
        var byId = new Dictionary<Guid, LivePayment>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.id, p.intent_id, p.status, p.version, h.status, coalesce(h.amount_minor, 0), c.status,
                   EXISTS (SELECT 1 FROM revenant.ledger_group g WHERE g.payment_id = p.id AND g.purpose = 'Settlement'),
                   EXISTS (SELECT 1 FROM revenant.ledger_group g WHERE g.payment_id = p.id AND g.purpose = 'Return')
              FROM revenant.payment p
              LEFT JOIN revenant.hold h ON h.payment_id = p.id
              LEFT JOIN revenant.review_case c ON c.payment_id = p.id
             WHERE p.scenario_run_id = @run
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue("run", run);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var p = new LivePayment(r.GetString(1), Enum.Parse<PaymentStatus>(r.GetString(2)), r.GetInt64(3), [],
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.IsDBNull(6) ? null : Enum.Parse<CaseStatus>(r.GetString(6)),
                    r.GetBoolean(7), r.GetBoolean(8));
                payments[p.IntentId] = p;
                byId[r.GetGuid(0)] = p;
            }
        }
        await using (var cmd = new NpgsqlCommand(
            "SELECT payment_id, to_status FROM revenant.payment_transition WHERE scenario_run_id = @run ORDER BY payment_id, version", connection, tx))
        {
            cmd.Parameters.AddWithValue("run", run);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                byId[r.GetGuid(0)].Trail.Add(Enum.Parse<PaymentStatus>(r.GetString(1)));
            }
        }
        await tx.CommitAsync(ct);
        return new LiveSnapshot(committed, closedAt, accounts, payments);
    }

    internal static List<ReconciliationMismatch> Reconcile(LedgerFinanceState source, LiveSnapshot live)
    {
        var mismatches = new List<ReconciliationMismatch>();
        void Check<T>(string subject, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                mismatches.Add(new ReconciliationMismatch(subject, $"{expected}", $"{actual}"));
            }
        }
        foreach (var account in source.Accounts.Values)
        {
            var found = live.Accounts.TryGetValue(account.AccountId, out var row);
            Check($"account {account.AccountId} posted", account.PostedMinor, found ? row.Posted : long.MinValue);
            Check($"account {account.AccountId} reserved", account.ReservedMinor, found ? row.Reserved : long.MinValue);
        }
        Check("account count", source.Accounts.Count, live.Accounts.Count);
        Check("payment count", source.Payments.Count, live.Payments.Count);
        foreach (var p in source.Payments.Values)
        {
            var id = p.Intent.IntentId;
            if (!live.Payments.TryGetValue(id, out var l))
            {
                mismatches.Add(new ReconciliationMismatch($"payment {id}", "present", "absent"));
                continue;
            }
            Check($"payment {id} status", p.Status, l.Status);
            Check($"payment {id} version", (long)p.Trail.Length, l.Version);
            Check($"payment {id} trail", string.Join(">", p.Trail), string.Join(">", l.Trail));
            Check($"payment {id} reservation", p.ReservedMinor, l.HoldStatus == "Active" ? l.HoldAmount : 0);
            Check($"payment {id} case", p.Case?.ToString() ?? "none", l.Case?.ToString() ?? "none");
            Check($"payment {id} settlement posting", p.SettlementPosted, l.Settlement);
            Check($"payment {id} return posting", p.ReturnPosted, l.Return);
            // A hold row exists exactly when screening reserved (which also opened the case); it is resolved once.
            var expectedHold = p.ReservedMinor > 0 ? "Active" : p.Case is null ? "none" : p.SettlementPosted ? "Consumed" : "Released";
            Check($"payment {id} hold", expectedHold, l.HoldStatus ?? "none");
        }
        return mismatches;
    }

    internal static Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string tenantId, CancellationToken ct)
    {
        var cmd = new NpgsqlCommand("SELECT set_config('revenant.tenant_id', @tenant, true)", connection, tx);
        cmd.Parameters.AddWithValue("tenant", tenantId);
        return ExecuteAndDisposeAsync(cmd, ct);
    }

    private static async Task ExecuteAndDisposeAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using (cmd)
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static Result<T, ProjectionError> Fail<T>(ProjectionErrorCode code, string message) =>
        Result<T, ProjectionError>.Failure(new ProjectionError(code, message));

    private static Result<T, ProjectionError> TimelineFail<T>(TimelineError error) =>
        Fail<T>(ProjectionErrorCode.Timeline, $"{error.Code}: {error.Message}");
}

/// <summary>A stored fact whose payload does not match its stored hash: the projection stops rather than replaying altered bytes.</summary>
public sealed class ProjectionIntegrityException(string message) : InvalidOperationException(message);
