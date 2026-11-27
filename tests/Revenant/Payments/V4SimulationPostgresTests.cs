using System.Security.Cryptography;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Contracts;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Revenant.Tests.Timeline;
using Revenant.Timeline;
using Xunit;
using LedgerSql = Revenant.Tests.Ledger.Ledger;

namespace Revenant.Tests.Payments;

[Collection(PaymentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class V4SimulationPostgresTests(LedgerDatabase db)
{
    private const string Tenant = PaymentKit.TenantA;
    private const string Owner = PaymentKit.Operator;
    private const long Origin = ObservedFinanceV4Tests.Origin;
    private const long Nine = ObservedFinanceV4Tests.Nine;
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Boundary_blocking_retry_and_journal_digest_survive_a_fresh_dispatcher()
    {
        var (run, context, initial) = await OpenAsync("01");
        var id = run.RunId;
        var before = Request("before", Nine - 1);
        var exact = Request("exact", Nine, V4CommandClass.ConditionalHumanRequest);
        var future = Request("future", Nine + 600_000, V4CommandClass.DerivedOpportunity);
        var cancelled = Request("cancelled", Nine - 1);
        var invalid = Request("invalid", Nine - 1);
        var store = new V4SimulationStore(db.Writer);
        Assert.Equal(1, (await store.AdmitAsync(Tenant, id, exact, Ct)).Sequence);
        Assert.Equal(2, (await store.AdmitAsync(Tenant, id, before, Ct)).Sequence);
        Assert.Equal(3, (await store.AdmitAsync(Tenant, id, future, Ct)).Sequence);
        Assert.Equal(4, (await store.AdmitAsync(Tenant, id, cancelled, Ct)).Sequence);
        Assert.Equal(5, (await store.AdmitAsync(Tenant, id, invalid, Ct)).Sequence);
        Assert.Equal(2, (await store.AdmitAsync(Tenant, id, before, Ct)).Sequence);
        var conflict = await Assert.ThrowsAsync<V4ScheduleException>(() => store.AdmitAsync(Tenant, id, before with { CanonicalPayload = [17] }, Ct));
        Assert.Equal(V4ScheduleError.ConflictingCommand, conflict.Error);
        Assert.Equal(V4StepKind.NotNextBoundary, (await store.SealAsync(Tenant, id, Nine, Ct)).Kind);

        var first = RequireSeal(await store.AdvanceOneStepAsync(Tenant, id, Ct), 1, Origin + 300_000);
        var unsupported = await store.AdvanceOneStepAsync(Tenant, id, Ct);
        Assert.Equal(V4StepKind.UnsupportedHandler, unsupported.Kind);
        Assert.Equal(("before", V4CommandStatus.Pending), (unsupported.Command!.Request.Key, unsupported.Command.Status));

        var blockedStore = new V4SimulationStore(db.Writer, (_, _) => Reply(V4CommandStatus.RetryableBlocked));
        var blocked = await blockedStore.AdvanceOneStepAsync(Tenant, id, Ct);
        Assert.Equal(V4StepKind.Blocked, blocked.Kind);
        Assert.Equal(("before", Origin, Nine - 1, (long?)null),
            (blocked.Command!.Request.Key, blocked.Command.AdmittedTimeMs, blocked.Command.Request.DueTimeMs, blocked.Command.EffectiveTimeMs));
        Assert.Equal("test-retryableblocked", blocked.Command.OutcomeCode);
        Assert.Equal(blocked.Command.OutcomeSha256, (await store.SealAsync(Tenant, id, Nine, Ct)).Command!.OutcomeSha256);
        var late = await Assert.ThrowsAsync<V4ScheduleException>(() => store.AdmitAsync(Tenant, id, Request("too-late", Nine - 2), Ct));
        Assert.Equal(V4ScheduleError.InvalidCommand, late.Error);
        Assert.False(await store.CanCloseAsync(Tenant, id, Ct));
        var (otherRun, _, _) = await OpenAsync("08");
        RequireSeal(await store.AdvanceOneStepAsync(Tenant, otherRun.RunId, Ct), 1, Origin + 300_000);

        var active = new V4SimulationStore(db.Writer, (command, _) => Reply(command.Request.Key switch
        {
            "cancelled" => V4CommandStatus.Cancelled,
            "invalid" => V4CommandStatus.FailedPermanent,
            _ => V4CommandStatus.Completed,
        }));
        Assert.Equal(V4StepKind.Blocked, (await active.AdvanceOneStepAsync(Tenant, id, Ct)).Kind);
        await active.RetryAsync(Tenant, id, before.Key, Ct);
        Assert.Equal(V4StepKind.WorkDue, (await active.SealAsync(Tenant, id, Nine, Ct)).Kind);
        var completed = await active.AdvanceOneStepAsync(Tenant, id, Ct);
        Assert.Equal(V4StepKind.ProcessedCommand, completed.Kind);
        Assert.Equal(("before", Nine - 1), (completed.Command!.Request.Key, completed.Command.EffectiveTimeMs));
        Assert.Equal(completed.Command.OutcomeSha256, (await store.AdmitAsync(Tenant, id, before, Ct)).OutcomeSha256);
        Assert.Equal(V4CommandStatus.Cancelled, (await active.AdvanceOneStepAsync(Tenant, id, Ct)).Command!.Status);
        Assert.Equal(V4CommandStatus.FailedPermanent, (await active.AdvanceOneStepAsync(Tenant, id, Ct)).Command!.Status);

        var nine = RequireSeal(await active.AdvanceOneStepAsync(Tenant, id, Ct), 2, Nine);
        var nineSeal = ObservationSealCodec.Decode(nine.Payload.AsSpan());
        var replayRole = new V4SimulationStore(db.Replay);
        var journal = await replayRole.ReadJournalAsync(Tenant, id, Ct);
        Assert.Equal(nineSeal.ResolvedCommandDigestSha256, V4SimulationScheduler.RecomputeCommandDigest(id, Nine, journal));
        Assert.Equal(initial.PrefixDigest, ObservationSealCodec.Decode(first.Payload.AsSpan()).PrefixFactDigestSha256);
        Assert.Equal(new[] { "exact", "future" }, journal.Where(c => c.Status == V4CommandStatus.Pending).Select(c => c.Request.Key));
        var atBoundary = await active.AdvanceOneStepAsync(Tenant, id, Ct);
        Assert.Equal((V4StepKind.ProcessedCommand, "exact", Nine), (atBoundary.Kind, atBoundary.Command!.Request.Key, atBoundary.Command.EffectiveTimeMs));
        Assert.Equal(nineSeal.ResolvedCommandDigestSha256,
            V4SimulationScheduler.RecomputeCommandDigest(id, Nine, await replayRole.ReadJournalAsync(Tenant, id, Ct)));
        Assert.Equal(future.CanonicalPayload.ToArray(), journal.Single(c => c.Request.Key == future.Key).Request.CanonicalPayload.ToArray());
        Assert.Equal(V4StepKind.NotNextBoundary, (await store.SealAsync(Tenant, id, Origin, Ct)).Kind);
        Assert.Equal(V4StepKind.NotNextBoundary, (await store.SealAsync(Tenant, id, Nine + 1, Ct)).Kind);
        Assert.Equal(nine.Payload.ToArray(), RequireSeal(await store.SealAsync(Tenant, id, Nine, Ct), 2, Nine).Payload.ToArray());
        Assert.Equal(0L, await PaymentKit.Count(db.Owner, "payment", id.Value));

        var pinned = await replayRole.ReadPinnedContextAsync(Tenant, id, Ct);
        Assert.Equal(ObservedFinanceContextCodec.Instance.EncodeCanonical(context), ObservedFinanceContextCodec.Instance.EncodeCanonical(pinned));
        var rebuilt = await replayRole.RebuildAsync(Tenant, run, pinned, Ct);
        Assert.Equal((2L, Nine, Nine), (rebuilt.LastFact, rebuilt.LastSealedBoundaryMs, rebuilt.LastEffectiveTimeMs));
        await AssertTimelineReplayAsync(initial, context, rebuilt, first, nine);
    }

    [Fact]
    public async Task Competing_dispatchers_share_one_boundary_while_another_run_advances()
    {
        var (run, _, _) = await OpenAsync("02");
        var (independent, _, _) = await OpenAsync("03");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var blocker = new V4SimulationStore(db.Writer, async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return await Reply(V4CommandStatus.Completed);
        });
        await using var otherPool = db.CreateWriterPool(5);
        var other = new V4SimulationStore(otherPool);
        var boundary = Origin + 300_000;
        await blocker.AdmitAsync(Tenant, run.RunId, Request("head", boundary - 1), Ct);
        var processing = blocker.AdvanceOneStepAsync(Tenant, run.RunId, timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var a = blocker.SealAsync(Tenant, run.RunId, boundary, timeout.Token);
            var b = other.SealAsync(Tenant, run.RunId, boundary, timeout.Token);
            RequireSeal(await other.SealAsync(Tenant, independent.RunId, boundary, timeout.Token), 1, boundary);
            Assert.False(a.IsCompleted);
            Assert.False(b.IsCompleted);
            release.SetResult();
            Assert.Equal(V4StepKind.ProcessedCommand, (await processing).Kind);
            var receipts = await Task.WhenAll(a, b);
            Assert.Equal(RequireSeal(receipts[0], 1, boundary).Payload.ToArray(), RequireSeal(receipts[1], 1, boundary).Payload.ToArray());
            Assert.Equal(1, await PaymentKit.Count(db.Owner, "finance_fact", run.RunId.Value));
            Assert.True(await other.CanCloseAsync(Tenant, run.RunId, Ct));
        }
        finally
        {
            release.TrySetResult();
            await processing;
        }
    }

    [Fact]
    public async Task Failed_commits_and_lost_acknowledgments_do_not_leave_partial_progress()
    {
        var (run, _, initial) = await OpenAsync("04");
        var id = run.RunId;
        var boundary = Origin + 300_000;
        var store = new V4SimulationStore(db.Writer);
        await using (var inject = db.Owner.CreateCommand($"""
            CREATE FUNCTION revenant.test_v4_commit_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              RAISE EXCEPTION USING ERRCODE = 'RV098', MESSAGE = 'injected before commit';
            END $$;
            CREATE CONSTRAINT TRIGGER test_v4_commit_failure AFTER UPDATE ON revenant.v4_scheduler_run
              DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
              WHEN (NEW.run_id = '{id.Value:D}'::uuid)
              EXECUTE FUNCTION revenant.test_v4_commit_failure();
            """))
        {
            await inject.ExecuteNonQueryAsync(Ct);
        }
        try
        {
            var crash = await Assert.ThrowsAsync<PostgresException>(() => store.SealAsync(Tenant, id, boundary, Ct));
            Assert.Equal("RV098", crash.SqlState);
            Assert.Equal(0, await PaymentKit.Count(db.Owner, "finance_fact", id.Value));
            await AssertProgressAsync(id, initial.LastFact, Origin);
        }
        finally
        {
            await using var drop = db.Owner.CreateCommand("""
                DROP TRIGGER test_v4_commit_failure ON revenant.v4_scheduler_run;
                DROP FUNCTION revenant.test_v4_commit_failure();
                """);
            await drop.ExecuteNonQueryAsync(Ct);
        }

        var request = Request("fault", boundary - 1);
        await store.AdmitAsync(Tenant, id, request, Ct);
        var crashing = new V4SimulationStore(db.Writer, (_, _) => throw new InvalidOperationException("dispatcher died before commit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashing.AdvanceOneStepAsync(Tenant, id, Ct));
        Assert.Equal(V4CommandStatus.Pending, (await store.AdmitAsync(Tenant, id, request, Ct)).Status);
        await AssertProgressAsync(id, 0, Origin);

        var recovered = new V4SimulationStore(db.Writer, (_, _) => Reply(V4CommandStatus.Completed));
        _ = await recovered.AdvanceOneStepAsync(Tenant, id, Ct);
        _ = await recovered.SealAsync(Tenant, id, boundary, Ct);
        Assert.Equal(V4CommandStatus.Completed, (await store.AdmitAsync(Tenant, id, request, Ct)).Status);
        RequireSeal(await store.SealAsync(Tenant, id, boundary, Ct), 1, boundary);
        Assert.Equal(1, await PaymentKit.Count(db.Owner, "finance_fact", id.Value));
        Assert.Single(await store.ReadJournalAsync(Tenant, id, Ct));
        await AssertProgressAsync(id, 1, boundary);
    }

    [Fact]
    public async Task Tenant_roles_and_constraints_reject_inconsistent_direct_writes()
    {
        var (run, _, initial) = await OpenAsync("05");
        var id = run.RunId;
        var store = new V4SimulationStore(db.Writer);
        var boundary = Origin + 300_000;
        Assert.Contains("0009_v4_causal_scheduler", db.MigrateOutput);

        await AssertSqlErrorAsync(db.Writer, Tenant, "RV011", async conn =>
        {
            await AdvanceCursorAsync(conn, id, 1, boundary);
        });
        await AssertProgressAsync(id, 0, Origin);

        var seal = ObservedFinanceInput.Seal(new(id, boundary, Origin, 0, initial.PrefixDigest, V4SimulationScheduler.InitialCommandDigest(id)));
        await AssertSqlErrorAsync(db.Writer, Tenant, "RV011", conn => InsertSealAsync(conn, seal, null)); // missing cursor advancement
        await AssertSqlErrorAsync(db.Writer, Tenant, PostgresErrorCodes.CheckViolation, async conn =>
        {
            await AdvanceCursorAsync(conn, id, 1, boundary);
            await InsertSealAsync(conn, seal, id.Value); // seal may never reference a payment
        });
        await AssertSqlErrorAsync(db.Writer, Tenant, "RV011", async conn =>
        {
            await AdvanceCursorAsync(conn, id, 1, boundary);
            await InsertSealAsync(conn, seal, null); // missing watermark advancement
        });

        var command = Request("tenant-test", boundary);
        await store.AdmitAsync(Tenant, id, command, Ct);
        await AssertSqlErrorAsync(db.Writer, Tenant, "RV011", conn => LedgerSql.ExecAsync(conn, """
            UPDATE revenant.v4_scheduled_command SET status = 'Completed', effective_time_ms = due_time_ms,
              outcome_code = 'illegal-early', outcome_sha256 = sha256('early'::bytea) WHERE run_id = @r
            """, ("r", id.Value)));
        await using (var wrong = await db.Writer.OpenConnectionAsync(Ct))
        await using (var tx = await LedgerSql.BeginTenantAsync(wrong, PaymentKit.TenantB))
        {
            foreach (var table in new[] { "v4_scheduler_run", "v4_scheduled_command" })
            {
                Assert.Equal(0, await LedgerSql.ScalarAsync<long>(wrong, $"SELECT count(*) FROM revenant.{table} WHERE run_id = @r", ("r", id.Value)));
            }
            Assert.Equal(0, await LedgerSql.ExecAsync(wrong, "UPDATE revenant.v4_scheduled_command SET status = 'Pending' WHERE run_id = @r", ("r", id.Value)));
            Assert.Equal(0, await LedgerSql.ExecAsync(wrong, "UPDATE revenant.v4_scheduler_run SET clock_ms = 0 WHERE run_id = @r", ("r", id.Value)));
        }
        Assert.Equal(V4ScheduleError.RunNotFound, (await Assert.ThrowsAsync<V4ScheduleException>(() => store.AdmitAsync(PaymentKit.TenantB, id, command, Ct))).Error);
        await AssertSqlErrorAsync(db.Writer, PaymentKit.TenantB, "RV011", conn => LedgerSql.ExecAsync(conn, """
            INSERT INTO revenant.v4_scheduled_command
              (run_id, tenant_id, command_seq, command_key, command_class, command_kind, payload_schema, canonical_payload,
               payload_sha256, requested_time_ms, admitted_time_ms, due_time_ms, status)
            VALUES (@r, @tenant, 2, 'wrong-tenant', 'Exogenous', 'test-control', 'test-command/1', 'x'::bytea,
               sha256('x'::bytea), @time, @time, @time, 'Pending')
            """, ("r", id.Value), ("tenant", Tenant), ("time", Origin)));
        await AssertSqlErrorAsync(db.Writer, PaymentKit.TenantB, "RV011", conn => LedgerSql.ExecAsync(conn, """
            INSERT INTO revenant.v4_scheduler_run
              (run_id, tenant_id, origin_ms, watermark_exclusive_ms, clock_ms, next_command_seq, status,
               scheduler_contract_id, command_completion_digest, canonical_context)
            VALUES (@r, @tenant, @time, @time, @time, 1, 'Open', 'sim-scheduler/1', @digest, 'x'::bytea)
            """, ("r", id.Value), ("tenant", Tenant), ("time", Origin),
            ("digest", Convert.FromHexString(V4SimulationScheduler.InitialCommandDigest(id)))));

        foreach (var table in new[] { "v4_scheduler_run", "v4_scheduled_command" })
        {
            await AssertSqlErrorAsync(db.Replay, Tenant, PostgresErrorCodes.InsufficientPrivilege,
                conn => LedgerSql.ExecAsync(conn, $"DELETE FROM revenant.{table} WHERE run_id = @r", ("r", id.Value)));
        }
        await AssertSqlErrorAsync(db.Replay, Tenant, PostgresErrorCodes.InsufficientPrivilege, conn => InsertSealAsync(conn, seal, null));
        RequireSeal(await store.SealAsync(Tenant, id, boundary, Ct), 1, boundary);
        await AssertSqlErrorAsync(db.Writer, Tenant, "RV011", conn => InsertSealAsync(conn, seal, null));
        await AssertProgressAsync(id, 1, boundary);

        var signed = ObservedFinanceV4Tests.Start(new(Guid.Parse("00000000-0000-0000-0000-000000009007")), -300_000);
        await store.OpenTestRunAsync(Tenant, Owner, signed.Run, signed.Context, Ct);
        RequireSeal(await store.SealAsync(Tenant, signed.Run.RunId, 0, Ct), 1, 0);
        await AssertProgressAsync(signed.Run.RunId, 1, 0);
    }

    [Fact]
    public async Task Upgrade_preserves_old_rows_and_produces_the_same_V4_bytes_as_a_fresh_schema()
    {
        await using var upgraded = await db.CreatePreV4DatabaseAsync();
        var legacy = PaymentKit.Store(upgraded);
        var historical = new List<(ScenarioRunId Id, bool Closed, string Fingerprint)>();
        foreach (var family in new[] { FinanceLedgerVersion.ReducerVersion, ModelLedgerVersion.ReducerVersion })
        {
            foreach (var closed in new[] { false, true })
            {
                var id = new ScenarioRunId(Guid.NewGuid());
                Assert.True((await legacy.OpenRunAsync(Tenant, Owner, "operator", 42, id, Ct, family)).IsSuccess);
                if (closed)
                {
                    Assert.True((await legacy.CloseRunAsync(Tenant, Owner, id, "close-before-upgrade", Ct)).IsSuccess);
                }
                else
                {
                    var intent = PaymentKit.Intents(id)[0];
                    Assert.True((await legacy.SubmitIntentAsync(Tenant, Owner, intent, "intake-before-upgrade", Ct)).IsSuccess);
                }
                historical.Add((id, closed, await LegacyFingerprintAsync(upgraded, id)));
            }
        }
        var r2Output = await db.MigrateToR2Async();
        Assert.Contains("module revenant: applying 0009_v4_causal_scheduler", r2Output);
        Assert.Contains("done: 1 applied", r2Output);
        var r2 = ObservedFinanceV4Tests.Start(new(Guid.Parse("00000000-0000-0000-0000-000000009008")));
        var r2Store = new V4SimulationStore(upgraded, (_, _) => Reply(V4CommandStatus.Completed));
        await r2Store.OpenTestRunAsync(Tenant, Owner, r2.Run, r2.Context, Ct);
        var boundary = Origin + 300_000;
        var request = Request("before-r3", boundary - 1);
        _ = await r2Store.AdmitAsync(Tenant, r2.Run.RunId, request, Ct);
        Assert.Equal(V4StepKind.ProcessedCommand, (await r2Store.AdvanceOneStepAsync(Tenant, r2.Run.RunId, Ct)).Kind);
        var journal = await r2Store.ReadJournalAsync(Tenant, r2.Run.RunId, Ct);
        var commandDigest = V4SimulationScheduler.RecomputeCommandDigest(r2.Run.RunId, boundary, journal);
        var r2Seal = ObservedFinanceInput.Seal(new(r2.Run.RunId, boundary, Origin, 0, r2.State.PrefixDigest, commandDigest));
        // Append through the original 0009 SQL guards before the branch table exists.
        await using (var conn = await upgraded.OpenConnectionAsync(Ct))
        await using (var tx = await LedgerSql.BeginTenantAsync(conn, Tenant))
        {
            await AdvanceCursorAsync(conn, r2.Run.RunId, 1, boundary);
            await InsertSealAsync(conn, r2Seal, null);
            await LedgerSql.ExecAsync(conn, """
                UPDATE revenant.v4_scheduler_run SET watermark_exclusive_ms = @time, clock_ms = @time WHERE run_id = @run
                """, ("time", boundary), ("run", r2.Run.RunId.Value));
            await tx.CommitAsync(Ct);
        }
        var r2Fingerprint = await LegacyFingerprintAsync(upgraded, r2.Run.RunId);

        var output = await db.MigrateV4UpgradeDatabaseAsync();
        Assert.Contains("module revenant: applying 0010_v4_branch_execution", output);
        Assert.Contains("module revenant: applying 0011_v4_observation_projections", output);
        Assert.Contains("module revenant: applying 0012_v4_model_screening", output);
        Assert.Contains("module revenant: applying 0013_v4_advisory_evidence", output);
        Assert.Contains("module revenant: applying 0014_r7_advisory_evidence_v2", output);
        Assert.Contains("module revenant: applying 0015_r7_liquidity_projection_evidence", output);
        Assert.Contains("module revenant: applying 0016_counterfactual", output);
        Assert.Contains("module revenant: applying 0017_replay_context_isolation", output);
        Assert.Contains("module revenant: applying 0018_replay_scheduler_context_isolation", output);
        Assert.Contains("module revenant: applying 0019_preserve_marrow_owner_guards", output);
        Assert.Contains("done: 10 applied", output);
        Assert.Equal(r2Fingerprint, await LegacyFingerprintAsync(upgraded, r2.Run.RunId));
        var preserved = RequireSeal(await r2Store.SealAsync(Tenant, r2.Run.RunId, boundary, Ct), 1, boundary);
        Assert.Equal(ObservedFinanceInputCodec.Instance.EncodeCanonical(r2Seal), ObservedFinanceInputCodec.Instance.EncodeCanonical(preserved));
        Assert.Equal(V4CommandStatus.Completed, (await r2Store.AdmitAsync(Tenant, r2.Run.RunId, request, Ct)).Status);
        var nextBoundary = boundary + 300_000;
        RequireSeal(await r2Store.SealAsync(Tenant, r2.Run.RunId, nextBoundary, Ct), 2, nextBoundary);

        foreach (var snapshot in historical)
        {
            Assert.Equal(snapshot.Fingerprint, await LegacyFingerprintAsync(upgraded, snapshot.Id));
        }

        foreach (var snapshot in historical.Where(s => !s.Closed))
        {
            var fake = ObservedFinanceInput.Seal(new(snapshot.Id, Nine, Nine - 300_000, 1,
                ObservedFinanceV4Tests.EmptyDigest, ObservedFinanceV4Tests.EmptyDigest));
            await AssertSqlErrorAsync(upgraded, Tenant, "RV011", conn => InsertSealAsync(conn, fake, null));
        }

        var modelAwareOpen = historical[2].Id;
        await AssertSqlErrorAsync(upgraded, Tenant, "RV010", async conn =>
        {
            await AdvanceCursorAsync(conn, modelAwareOpen, 2, Nine);
            await LedgerSql.ExecAsync(conn, """
                INSERT INTO revenant.finance_fact (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id, canonical, canonical_sha256)
                SELECT tenant_id, scenario_run_id, 2, 'Screening', 'revenant-screening/1', payment_id, canonical, canonical_sha256
                FROM revenant.finance_fact WHERE scenario_run_id = @run AND fact_seq = 1
                """, ("run", modelAwareOpen.Value));
        });
        Assert.Equal(historical[2].Fingerprint, await LegacyFingerprintAsync(upgraded, modelAwareOpen));

        var fixture = ObservedFinanceV4Tests.Start(new(Guid.Parse("00000000-0000-0000-0000-000000009006")));
        var freshBytes = await FixedScheduleAsync(db.Writer, fixture.Run, fixture.Context, pauseBetweenSteps: false);
        var upgradedBytes = await FixedScheduleAsync(upgraded, fixture.Run, fixture.Context, pauseBetweenSteps: true);
        Assert.Equal(freshBytes.Seals, upgradedBytes.Seals);
        Assert.Equal(freshBytes.State, upgradedBytes.State);
    }

    private static async Task<(string[] Seals, byte[] State)> FixedScheduleAsync(NpgsqlDataSource source,
        ScenarioRun run, ObservedFinanceContext context, bool pauseBetweenSteps)
    {
        var first = new V4SimulationStore(source, (_, _) => Reply(V4CommandStatus.Completed));
        await first.OpenTestRunAsync(Tenant, Owner, run, context, Ct);
        await first.AdmitAsync(Tenant, run.RunId, Request("boundary", Nine), Ct);
        await first.AdmitAsync(Tenant, run.RunId, Request("earlier", Nine - 1), Ct);
        var seals = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var dispatcher = pauseBetweenSteps ? new V4SimulationStore(source, async (_, _) =>
            {
                await Task.Yield();
                return await Reply(V4CommandStatus.Completed);
            }) : first;
            var step = await dispatcher.AdvanceOneStepAsync(Tenant, run.RunId, Ct);
            if (step.Seal is { } seal)
            {
                seals.Add(Convert.ToHexString(ObservedFinanceInputCodec.Instance.EncodeCanonical(seal)));
            }
            else
            {
                Assert.Equal(V4StepKind.ProcessedCommand, step.Kind);
            }
        }
        Assert.Equal(3, seals.Count);
        var rebuilt = await first.RebuildAsync(Tenant, run, context, Ct);
        return (seals.ToArray(), ObservedFinanceStateCodec.Instance.EncodeCanonical(rebuilt));
    }

    private static async Task<string> LegacyFingerprintAsync(NpgsqlDataSource source, ScenarioRunId id)
    {
        await using var conn = await source.OpenConnectionAsync(Ct);
        await using var tx = await LedgerSql.BeginTenantAsync(conn, Tenant);
        return await LedgerSql.ScalarAsync<string>(conn, """
            SELECT row_to_json(r)::text || row_to_json(c)::text ||
                coalesce((SELECT string_agg(fact_seq || ':' || encode(canonical, 'hex') || ':' || encode(canonical_sha256, 'hex'), ',' ORDER BY fact_seq)
                          FROM revenant.finance_fact WHERE scenario_run_id = r.id), '')
              FROM revenant.scenario_run r JOIN revenant.run_cursor c ON c.scenario_run_id = r.id WHERE r.id = @run
            """, ("run", id.Value));
    }

    private async Task AssertTimelineReplayAsync(ObservedFinanceState initial, ObservedFinanceContext context,
        ObservedFinanceState rebuilt, params ObservedFinanceInput[] facts)
    {
        var logins = await db.ProvisionDeployedLoginsAsync();
        ServiceProvider Host()
        {
            var services = new ServiceCollection();
            services.AddSingleton(_ => NpgsqlDataSource.Create(logins.Worker));
            services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
            return services.BuildServiceProvider();
        }

        TimelineId timeline;
        string stateHash;
        await using (var host = Host())
        {
            var engine = host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
            timeline = (await engine.CreateSourceAsync(new(Owner, ObservedFinanceTimelineVersions.V4, initial, context,
                context.BasePolicy.ConfigHash, context.ManifestSha256), "r2-create", Ct)).Value;
            foreach (var fact in facts)
            {
                var input = new OrderedInput<ObservedFinanceInput>(FinanceLedgerTransition.InputId(initial.RunId, fact.Sequence), 0, fact);
                Assert.True((await engine.AppendAsync(timeline, fact.Sequence - 1, new(fact.Sequence, [input]), input.InputId, Ct)).IsSuccess);
            }
            stateHash = (await engine.CompleteAsync(timeline, rebuilt.LastFact, Ct)).Value.FinalStateHash;
        }
        await using var fresh = Host();
        var replay = (await fresh.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>()
            .ReplayAsync(timeline, Ct)).Value;
        Assert.Equal(stateHash, replay.FinalStateHash);
        Assert.Equal(ObservedFinanceStateCodec.Instance.EncodeCanonical(rebuilt), ObservedFinanceStateCodec.Instance.EncodeCanonical(replay.FinalState));
    }

    private async Task<(ScenarioRun Run, ObservedFinanceContext Context, ObservedFinanceState State)> OpenAsync(string suffix)
    {
        var fixture = ObservedFinanceV4Tests.Start(new(Guid.Parse($"00000000-0000-0000-0000-0000000090{suffix}")));
        await new V4SimulationStore(db.Writer).OpenTestRunAsync(Tenant, Owner, fixture.Run, fixture.Context, Ct);
        return fixture;
    }

    private async Task AssertProgressAsync(ScenarioRunId run, long sequence, long boundary)
    {
        Assert.Equal(sequence, await PaymentKit.Scalar<long>(db.Owner, "SELECT last_fact_seq FROM revenant.run_cursor WHERE scenario_run_id = @r", ("r", run.Value)));
        Assert.Equal(boundary, await PaymentKit.Scalar<long>(db.Owner, "SELECT last_event_time_ms FROM revenant.run_cursor WHERE scenario_run_id = @r", ("r", run.Value)));
        Assert.Equal(boundary, await PaymentKit.Scalar<long>(db.Owner, "SELECT watermark_exclusive_ms FROM revenant.v4_scheduler_run WHERE run_id = @r", ("r", run.Value)));
    }

    private static V4CommandRequest Request(string key, long due, V4CommandClass kind = V4CommandClass.Exogenous) =>
        new(key, kind, "test-control", "test-command/1", [.. new CanonicalWriter().WriteInt32(1).WriteString(key).ToArray()], Origin - 120_000, due);

    private static Task<V4CommandOutcome?> Reply(V4CommandStatus status) =>
        Task.FromResult<V4CommandOutcome?>(new(status, "test-" + status.ToString().ToLowerInvariant(), [1]));

    private static ObservedFinanceInput RequireSeal(V4Step step, long sequence, long boundary)
    {
        Assert.Equal(V4StepKind.Sealed, step.Kind);
        var seal = Assert.IsType<ObservedFinanceInput>(step.Seal);
        Assert.Equal((sequence, boundary), (seal.Sequence, seal.EffectiveTimeMs));
        return seal;
    }

    private static Task AdvanceCursorAsync(NpgsqlConnection conn, ScenarioRunId id, long seq, long time) =>
        LedgerSql.ExecAsync(conn, "UPDATE revenant.run_cursor SET last_fact_seq = @seq, last_event_time_ms = @ms WHERE scenario_run_id = @run",
            ("seq", seq), ("ms", time), ("run", id.Value));

    private static Task InsertSealAsync(NpgsqlConnection conn, ObservedFinanceInput seal, Guid? payment) =>
        LedgerSql.ExecAsync(conn, """
            INSERT INTO revenant.finance_fact (tenant_id, scenario_run_id, fact_seq, kind, schema_version, payment_id,
              canonical, canonical_sha256, effective_time_ms)
            VALUES (@tenant, @run, @seq, 'ObservationSeal', @schema, CAST(NULLIF(@payment, '') AS uuid), @payload, @sha, @time)
            """, ("tenant", Tenant), ("run", seal.RunId.Value), ("seq", seal.Sequence), ("schema", seal.PayloadSchema),
            ("payment", payment?.ToString("D") ?? ""), ("payload", seal.Payload.ToArray()), ("sha", SHA256.HashData(seal.Payload.AsSpan())), ("time", seal.EffectiveTimeMs));

    private static async Task AssertSqlErrorAsync(NpgsqlDataSource source, string tenant, string code, Func<NpgsqlConnection, Task> action)
    {
        await using var conn = await source.OpenConnectionAsync(Ct);
        await using var tx = await LedgerSql.BeginTenantAsync(conn, tenant);
        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await action(conn);
            await tx.CommitAsync(Ct);
        });
        Assert.Equal(code, error.SqlState);
    }
}
