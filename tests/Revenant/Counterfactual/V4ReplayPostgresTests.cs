using System.Reflection;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Hosts.Worker;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Api;
using Revenant.Contracts;
using Revenant.Counterfactual;
using Revenant.Finance;
using Revenant.Intelligence;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Revenant.Tests.Payments;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Counterfactual;

[Collection(PaymentCollection.Name)]
public sealed class V4ReplayPostgresTests(LedgerDatabase db, ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Missing_required_model_blocks_screening_and_missing_advisory_has_no_prediction()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        const string tenant = PaymentKit.TenantA;
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var data = NpgsqlDataSource.Create(logins.WorkerLedger);
        await using var host = Host(logins.Worker);
        var scratch = Directory.CreateTempSubdirectory("r8-missing-artifacts-");
        try
        {
            var artifact = Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts", BehavioralRiskScorerV2.ArtifactDirectory);
            File.Copy(Path.Combine(artifact, BehavioralRiskScorerV2.ManifestFile),
                Path.Combine(scratch.FullName, BehavioralRiskScorerV2.ManifestFile));
            var model = new Lazy<Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
                BehavioralRiskScorerV2.Load(scratch.FullName, BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
            var ledger = PaymentKit.Store(data);
            var store = new V4SimulationStore(data, ledger: ledger, model: model);
            var id = new ScenarioRunId(Guid.NewGuid());
            var origin = V4CausalPolicyTests.Origin;
            var cutoff = origin + V4ReviewCounts.WarmupMs;
            var run = BehavioralEpisodeScenario.Create(id, 46, origin, V4ReviewCounts.WarmupMs + 300_000, [2, 2, 2, 2, 2, 2]);
            var context = V4CausalPolicyTests.Context(run, PolicyCatalog.V7);
            await new V4BranchExecution(store, Engine(host)).OpenSourceAsync(tenant, PaymentKit.Operator, run, context, ct);
            for (var boundary = origin + 300_000; boundary <= cutoff; boundary += 300_000)
                Assert.Equal(V4StepKind.Sealed, (await store.SealAsync(tenant, id, boundary, ct)).Kind);

            var from = run.Manifest.Accounts[0];
            var to = run.Manifest.Accounts[2];
            var payment = new PaymentIntent(id, "missing-model", 1, cutoff, from.BankId, from.AccountId, to.BankId, to.AccountId,
                Money.Usd(1_000), SyntheticTruth.Legitimate, new(false, false));
            await store.AdmitAsync(tenant, id, new("intake", V4CommandClass.Exogenous, "PaymentIntent", ScenarioSchemas.InputSchemaVersion,
                [.. PaymentIntentCodec.Instance.EncodeCanonical(payment)], cutoff, cutoff), ct);
            Assert.Equal(V4CommandStatus.Completed, (await store.AdvanceOneStepAsync(tenant, id, ct)).Command?.Status);
            var before = await store.ReadFinanceHistoryAsync(tenant, id, id, ct);
            await store.AdmitAsync(tenant, id, new("screen", V4CommandClass.DerivedOpportunity, "Screening", "v4-payment-opportunity/1",
                [.. new CanonicalWriter().WriteInt32(1).WriteString(payment.IntentId).ToArray()], cutoff, cutoff), ct);
            var blocked = await store.AdvanceOneStepAsync(tenant, id, ct);
            Assert.Equal(V4StepKind.Blocked, blocked.Kind);
            Assert.Equal("model-configuration-MissingArtifact", blocked.Command?.OutcomeCode);
            Assert.Equal(PaymentStatus.Initiated, (await ledger.GetPaymentAsync(tenant, id, payment.IntentId, ct))!.Status);
            Assert.Equal(before.LastFact, (await store.ReadFinanceHistoryAsync(tenant, id, id, ct)).LastFact);
            Assert.Equal(V4StepKind.Blocked, (await store.SealAsync(tenant, id, cutoff + 300_000, ct)).Kind);

            var series = new V4AdvisorySeries(V4AdvisorySeries.AccountSchema, id, from.AccountId, "count", origin, null,
                origin + 300_000, AdvisorySeriesCoverage.Complete, [new(new(id, origin, origin + 300_000, 1, new string('a', 64)), 0)]);
            var unavailable = RevenantAdvisoryWorker.ScoreSupported(series, id, scratch.FullName);
            Assert.Equal(AdvisoryStatus.ArtifactMissing, unavailable.Status);
            Assert.Null(unavailable.AnomalyV2);
            Assert.Null(unavailable.Liquidity);
            var receipt = unavailable.Encode();
            var decoded = R8Service.DecodeAlert(receipt, ObservationEncoding.Hash(receipt), id, origin + 300_000);
            Assert.Equal(AdvisoryStatus.ArtifactMissing, decoded.Status);
            Assert.Null(decoded.Alert);
        }
        finally { scratch.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Replay_login_forks_only_its_tenant_and_publishes_verified_readback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        const string tenant = PaymentKit.TenantA;
        const string actor = PaymentKit.Operator;
        const string secret = "replayTestOnlyPassword000001";
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        await using var live = Host(logins.Worker);
        var replayConnection = new NpgsqlConnectionStringBuilder(logins.Worker)
        { Username = "sands_replay", Password = secret, Options = $"-c revenant.tenant_id={tenant}" }.ConnectionString;
        var liveEngine = Engine(live);
        var reader = new V4SimulationStore(writer, ledger: PaymentKit.Store(writer));
        var root = new V4BranchExecution(reader, liveEngine);
        var runId = new ScenarioRunId(Guid.NewGuid());
        var origin = V4CausalPolicyTests.Origin;
        var run = BehavioralEpisodeScenario.Create(runId, 42, origin, 600_000, [2, 2, 2, 2, 2, 2]);
        var binding = await root.OpenSourceAsync(tenant, actor, run, V4CausalPolicyTests.Context(run, PolicyCatalog.V7), ct);
        var forkTime = origin + 300_000;
        Assert.Equal(V4StepKind.Sealed, (await reader.SealAsync(tenant, runId, forkTime, ct)).Kind);
        var from = run.Manifest.Accounts[0];
        var to = run.Manifest.Accounts[2];
        var p = new PaymentIntent(runId, "causal-r8", 1, forkTime, from.BankId, from.AccountId, to.BankId, to.AccountId,
            Money.Usd(150_000), SyntheticTruth.Legitimate, new(true, false));
        await Dispatch("PaymentIntent", V4CommandClass.Exogenous, ScenarioSchemas.InputSchemaVersion,
            PaymentIntentCodec.Instance.EncodeCanonical(p));
        var stepPayload = new CanonicalWriter().WriteInt32(1).WriteString(p.IntentId).ToArray();
        await Dispatch("Screening", V4CommandClass.DerivedOpportunity, "v4-payment-opportunity/1", stepPayload);
        await Dispatch("Settlement", V4CommandClass.DerivedOpportunity, "v4-payment-opportunity/1", stepPayload);
        Assert.Equal(V4StepKind.Sealed, (await reader.SealAsync(tenant, runId, origin + 600_000, ct)).Kind);

        async Task Dispatch(string kind, V4CommandClass kindClass, string schema, byte[] payload)
        {
            await reader.AdmitAsync(tenant, runId, new($"fixture:{kind}", kindClass, kind, schema, [.. payload], forkTime, forkTime), ct);
            var step = await reader.AdvanceOneStepAsync(tenant, runId, ct);
            Assert.Equal(V4CommandStatus.Completed, step.Command?.Status);
        }
        var original = await root.CompleteAsync(tenant, runId, ct);
        var otherRun = BehavioralEpisodeScenario.Create(new(Guid.NewGuid()), 43, origin, 600_000, [2, 2, 2, 2, 2, 2]);
        var other = await root.OpenSourceAsync(PaymentKit.TenantB, actor, otherRun, V4CausalPolicyTests.Context(otherRun, PolicyCatalog.V7), ct);
        var localRun = BehavioralEpisodeScenario.Create(new(Guid.NewGuid()), 44, origin, 600_000, [2, 2, 2, 2, 2, 2]);
        var ghost = live.GetRequiredService<ITimelineEngine<GameState, GameInput, GameContext>>();
        var ghostContext = GameContext.Create(42, 1);
        var ghostSource = (await ghost.CreateSourceAsync(new("ghost-fixture", GhostLapTimelineVersions.V1,
            GameState.Initial(ghostContext), ghostContext, "ghost-rules", "ghost-seed"), "ghost-source", ct)).Value;
        Assert.True((await ghost.CompleteAsync(ghostSource, 0, ct)).IsSuccess);
        await db.ApplyReplayContextPolicyAsync();
        await db.ProvisionReplayLoginAsync(tenant, secret);
        await using var replay = Host(replayConnection);
        var local = await root.OpenSourceAsync(tenant, actor, localRun, V4CausalPolicyTests.Context(localRun, PolicyCatalog.V7), ct);
        Assert.True((await liveEngine.RestoreAsync(local.TimelineId, 0, ct)).IsSuccess);
        Assert.Equal(V4StepKind.Sealed, (await reader.SealAsync(tenant, localRun.RunId, origin + 300_000, ct)).Kind);
        await root.CompleteAsync(tenant, localRun.RunId, ct);
        var ghostChild = (await ghost.ForkAsync(ghostSource, 0, "ghost-fork", GameContext.Create(43, 1), ct)).Value;
        Assert.Equal((await ghost.RestoreAsync(ghostSource, 0, ct)).Value.StateHash,
            (await ghost.RestoreAsync(ghostChild, 0, ct)).Value.StateHash);
        var sourceRef = await ContextRef(binding.TimelineId);
        var foreignRef = await ContextRef(other.TimelineId);
        var localRef = await ContextRef(local.TimelineId);
        var ghostRef = await ContextRef(ghostChild);
        Assert.Equal(4, new[] { sourceRef, foreignRef, localRef, ghostRef }.Distinct().Count());
        Assert.Equal(original.LastRecordHash, (await liveEngine.ReplayAsync(binding.TimelineId, ct)).Value.LastRecordHash);

        async Task<string> ContextRef(TimelineId timeline)
        {
            await using var query = db.Owner.CreateCommand("SELECT context_ref FROM marrow_timeline.histories WHERE timeline_id=@id");
            query.Parameters.AddWithValue("id", timeline.Value);
            return (string)(await query.ExecuteScalarAsync(ct))!;
        }
        var request = new V4ReplayRequest(1, origin + 600_000, PolicyCatalog.V8Id, V4ComparisonMode.PolicyOnly, []);
        var job = await V4ReplayWorker.SubmitAsync(writer, tenant, actor, runId, binding.TimelineId, "r8-proof", request, ct);
        var duplicate = await V4ReplayWorker.SubmitAsync(writer, tenant, actor, runId, binding.TimelineId, "r8-proof", request, ct);
        Assert.Equal(job.Id, duplicate.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => V4ReplayWorker.SubmitAsync(writer, tenant, actor, runId,
            binding.TimelineId, "r8-proof", request with { HorizonMs = origin + 300_000 }, ct));
        var replayData = replay.GetRequiredService<NpgsqlDataSource>();
        var worker = new V4ReplayWorker(replayData, Engine(replay));
        await using (var c = await replayData.OpenConnectionAsync(ct))
        {
            await using var inspect = new NpgsqlCommand("""
                SELECT has_table_privilege('marrow_timeline.context_artifacts','SELECT,INSERT')
                  AND NOT pg_has_role('marrow_timeline_runtime','SET')
                  AND NOT EXISTS (SELECT FROM pg_roles WHERE pg_has_role(oid,'MEMBER') AND (rolsuper OR rolbypassrls))
                """, c);
            Assert.Equal(true, await inspect.ExecuteScalarAsync(ct));
            await using var visible = new NpgsqlCommand("SELECT context_ref FROM marrow_timeline.context_artifacts", c);
            Assert.Equal(sourceRef, await visible.ExecuteScalarAsync(ct));
            await using (var rows = await visible.ExecuteReaderAsync(ct))
            {
                Assert.True(await rows.ReadAsync(ct));
                Assert.False(await rows.ReadAsync(ct));
            }
            foreach (var role in new[] { "sands_replay", "marrow_timeline_revenant_replay" })
            {
                await using var assume = new NpgsqlCommand($"SET ROLE {role}", c);
                await assume.ExecuteNonQueryAsync(ct);
                foreach (var deniedRef in new[] { foreignRef, localRef, ghostRef })
                {
                    await using var artifact = new NpgsqlCommand("SELECT canonical_bytes FROM marrow_timeline.context_artifacts WHERE context_ref=@ref", c);
                    artifact.Parameters.AddWithValue("ref", deniedRef);
                    Assert.Null(await artifact.ExecuteScalarAsync(ct));
                }
            }
            await using var tenantOverride = new NpgsqlCommand("SELECT set_config('revenant.tenant_id', @tenant, false)", c);
            tenantOverride.Parameters.AddWithValue("tenant", PaymentKit.TenantB);
            await tenantOverride.ExecuteNonQueryAsync(ct);
            Assert.Equal(sourceRef, await visible.ExecuteScalarAsync(ct));
            await using var unsafeRole = new NpgsqlCommand("SET ROLE marrow_timeline_runtime", c);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => unsafeRole.ExecuteNonQueryAsync(ct))).SqlState);
        }
        Assert.Null(await V4ReplayWorker.ReadAsync(replayData, PaymentKit.TenantB, job.Id, ct));
        // A provisioned worker can process any trusted job for its tenant. Its login cannot issue
        // a new job or redirect the immutable authorization of an existing one.
        await using (var c = await replayData.OpenConnectionAsync(ct))
        {
            foreach (var role in new[] { "sands_replay", "revenant_counterfactual_worker" })
            {
                await using var assume = new NpgsqlCommand($"SET ROLE {role}", c);
                await assume.ExecuteNonQueryAsync(ct);
                foreach (var sql in new[]
                {
                    """
                    INSERT INTO revenant.counterfactual_run
                      (id, tenant_id, actor_id, source_run_id, source_timeline_id, operation_key, request)
                    SELECT gen_random_uuid(), tenant_id, actor_id, @run, @timeline, 'unissued-fixture', request
                    FROM revenant.counterfactual_run WHERE id=@job
                    """,
                    "UPDATE revenant.counterfactual_run SET tenant_id=@tenant WHERE id=@job",
                    "UPDATE revenant.counterfactual_run SET source_run_id=@run, source_timeline_id=@timeline WHERE id=@job",
                    "UPDATE revenant.counterfactual_run SET request='{}'::jsonb, operation_key='unissued-fixture' WHERE id=@job",
                    "UPDATE revenant.counterfactual_worker_tenant SET tenant_id=@tenant WHERE login_name=session_user",
                    "INSERT INTO marrow_timeline.revenant_replay_source VALUES (@timeline,@tenant)",
                    "SELECT revenant.counterfactual_guard()",
                    "SELECT revenant.guard_replay_child_context()"
                })
                {
                    await using var attempt = new NpgsqlCommand(sql, c);
                    attempt.Parameters.AddWithValue("job", job.Id);
                    attempt.Parameters.AddWithValue("run", localRun.RunId.Value);
                    attempt.Parameters.AddWithValue("timeline", local.TimelineId.Value);
                    attempt.Parameters.AddWithValue("tenant", PaymentKit.TenantB);
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                        (await Assert.ThrowsAsync<PostgresException>(() => attempt.ExecuteNonQueryAsync(ct))).SqlState);
                }
            }
            await using (var reset = new NpgsqlCommand("RESET ROLE", c))
                await reset.ExecuteNonQueryAsync(ct);
            foreach (var reference in new[] { localRef, foreignRef })
            {
                await using var unauthorized = new NpgsqlCommand("""
                    SELECT canonical_bytes FROM marrow_timeline.context_artifacts WHERE context_ref=@ref
                      AND EXISTS (SELECT 1 FROM revenant.counterfactual_run WHERE id=@job)
                    """, c);
                unauthorized.Parameters.AddWithValue("ref", reference);
                unauthorized.Parameters.AddWithValue("job", job.Id);
                Assert.Null(await unauthorized.ExecuteScalarAsync(ct));
            }
            // Scheduler rows carry an independent copy of the exact same canonical context bytes.
            foreach (var role in new[] { "sands_replay", "revenant_replay_reader" })
            {
                await using var assume = new NpgsqlCommand($"SET ROLE {role}", c);
                await assume.ExecuteNonQueryAsync(ct);
                foreach (var (candidate, allowed) in new[] { (runId, true), (localRun.RunId, false), (otherRun.RunId, false) })
                {
                    await using var scheduler = new NpgsqlCommand(
                        "SELECT canonical_context FROM revenant.v4_scheduler_run WHERE run_id=@run", c);
                    scheduler.Parameters.AddWithValue("run", candidate.Value);
                    var bytes = await scheduler.ExecuteScalarAsync(ct);
                    if (allowed) Assert.IsType<byte[]>(bytes);
                    else Assert.Null(bytes);
                }
            }
        }
        foreach (var sql in new[]
        {
            "UPDATE revenant.ledger_entry SET amount_minor=amount_minor",
            "UPDATE revenant.hold SET status=status",
            "UPDATE revenant.review_case SET status=status",
            "UPDATE revenant.finance_fact SET canonical=canonical",
            "UPDATE revenant.audit_head SET last_seq=last_seq",
            "UPDATE marrow_timeline.histories SET status='Failed',failure_reason='forbidden' WHERE timeline_id=@source",
            "INSERT INTO marrow_timeline.steps SELECT * FROM marrow_timeline.steps WHERE timeline_id=@source LIMIT 1"
        })
        {
            await using var cmd = replayData.CreateCommand(sql);
            cmd.Parameters.AddWithValue("source", binding.TimelineId.Value);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));
            Assert.True(denied.SqlState == PostgresErrorCodes.InsufficientPrivilege
                || (sql.StartsWith("INSERT INTO marrow_timeline.steps", StringComparison.Ordinal) && denied.SqlState == "MT001"),
                $"{sql}: {denied.SqlState} {denied.MessageText}");
        }
        await using (var connection = await replayData.OpenConnectionAsync(ct))
        await using (var wrong = new NpgsqlCommand("SET ROLE revenant_replay_reader; SELECT set_config('revenant.tenant_id', @tenant, false);", connection))
        {
            wrong.Parameters.AddWithValue("tenant", PaymentKit.TenantB);
            await wrong.ExecuteNonQueryAsync(ct);
            await using var query = new NpgsqlCommand("SELECT count(*) FROM revenant.scenario_run WHERE id=@id", connection);
            query.Parameters.AddWithValue("id", otherRun.RunId.Value);
            Assert.Equal(0L, await query.ExecuteScalarAsync(ct));
        }
        // Even the replay credential cannot mark a job Verified without the matching durable Timeline.
        await using (var connection = await replayData.OpenConnectionAsync(ct))
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await using var start = new NpgsqlCommand("UPDATE revenant.counterfactual_run SET status='Running' WHERE id=@id", connection, tx);
            start.Parameters.AddWithValue("id", job.Id);
            Assert.Equal(1, await start.ExecuteNonQueryAsync(ct));
            var wrongContext = V4CausalPolicyTests.Context(localRun, PolicyCatalog.V8);
            var bytes = ObservedFinanceContextCodec.Instance.EncodeCanonical(wrongContext);
            var hash = V4Counterfactual.ContextDigest(wrongContext);
            await tx.SaveAsync("before_wrong_context", ct);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO marrow_timeline.context_artifacts(context_ref, context_schema_version, context_hash, canonical_bytes)
                VALUES ('sha256:' || @hash, 'revenant-finance-context/4', @hash, @bytes)
                ON CONFLICT DO NOTHING
                """, connection, tx);
            insert.Parameters.AddWithValue("hash", hash);
            insert.Parameters.AddWithValue("bytes", bytes);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(ct))).SqlState);
            await tx.RollbackAsync("before_wrong_context", ct);
            await using var forge = new NpgsqlCommand("UPDATE revenant.counterfactual_run SET status='Verified',result='{}'::jsonb WHERE id=@id", connection, tx);
            forge.Parameters.AddWithValue("id", job.Id);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            await tx.RollbackAsync(ct);
        }
        var interruptedEngine = DispatchProxy.Create<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>, InterruptAfterAppend>();
        ((InterruptAfterAppend)(object)interruptedEngine).Inner = Engine(replay);
        var interrupted = new V4ReplayWorker(replayData, interruptedEngine);
        await Assert.ThrowsAsync<OperationCanceledException>(() => interrupted.ProcessAsync(tenant, job.Id, ct));
        var incomplete = await V4ReplayWorker.ReadAsync(writer, tenant, job.Id, ct);
        Assert.Equal("Running", incomplete?.Status);
        Assert.Null(incomplete!.Result);
        await using var restarted = Host(replayConnection);
        var processed = await new V4ReplayWorker(restarted.GetRequiredService<NpgsqlDataSource>(), Engine(restarted))
            .ProcessAsync(tenant, job.Id, ct);
        var verified = await V4ReplayWorker.ReadAsync(writer, tenant, job.Id, ct);
        Assert.True(processed, verified?.Failure);
        Assert.Equal("Verified", verified?.Status);
        Assert.Contains(original.LastRecordHash, verified!.Result);
        Assert.Contains("\"netEffectMinor\": 0", verified.Result);
        Assert.Contains("\"status\": \"Held\"", verified.Result);
        Assert.Contains("\"status\": \"Settled\"", verified.Result);
        Assert.False(await worker.ProcessAsync(tenant, job.Id, ct));
        var again = (await liveEngine.ReplayAsync(binding.TimelineId, ct)).Value;
        Assert.Equal(original.LastRecordHash, again.LastRecordHash);
        Assert.Equal(original.FinalStateHash, again.FinalStateHash);
        var receipt = V4ReplayWorker.DecodeResult(verified.Result!);
        TimelineId child = new(receipt.ChildTimelineId);
        Assert.NotEqual(sourceRef, await ContextRef(child));
        await using var cold = Host(replayConnection);
        Assert.Equal(receipt.ChildTimelineId, (await Engine(cold).ReplayAsync(child, ct)).Value.Timeline.Value);
        Assert.Equal(receipt.ForkStateHash, (await Engine(cold).RestoreAsync(child, 1, ct)).Value.StateHash);
        await using var count = db.Owner.CreateCommand("""
            SELECT count(*) FROM marrow_timeline.steps s
            JOIN marrow_timeline.histories h ON h.timeline_id=s.timeline_id
            WHERE h.parent_timeline_id=@source AND s.position=2
            """);
        count.Parameters.AddWithValue("source", binding.TimelineId.Value);
        Assert.Equal(1L, await count.ExecuteScalarAsync(ct));
        output.WriteLine($"Source {binding.TimelineId.Value}: {original.LastRecordHash}; child {receipt.ChildTimelineId}: {receipt.ChildRecordHash}; job {job.Id}");
    }

    private static ServiceProvider Host(string connection)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        services.AddMarrowTimeline(GhostLapTimelineVersions.All, GhostLapTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider();
    }

    private static ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> Engine(ServiceProvider host) =>
        host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
}

public class InterruptAfterAppend : DispatchProxy
{
    public ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext> Inner { get; set; } = null!;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var result = method!.Invoke(Inner, args);
        return method.Name == nameof(Inner.AppendAsync)
            ? Interrupt((Task<Result<AppendReceipt, TimelineError>>)result!)
            : result;
    }

    private static async Task<Result<AppendReceipt, TimelineError>> Interrupt(Task<Result<AppendReceipt, TimelineError>> append)
    {
        var result = await append;
        Assert.True(result.IsSuccess);
        throw new OperationCanceledException("Fixture interrupted after the committed append.");
    }
}
