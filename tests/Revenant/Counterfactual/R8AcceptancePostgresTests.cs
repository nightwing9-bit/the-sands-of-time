using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Api;
using Revenant.Contracts;
using Revenant.Counterfactual;
using Revenant.Intelligence;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Revenant.Tests.Payments;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;

namespace Revenant.Tests.Counterfactual;

[Collection(PaymentCollection.Name)]
public sealed class R8AcceptancePostgresTests(LedgerDatabase db, ITestOutputHelper output)
{
    private const string Tenant = PaymentKit.TenantA;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Equal_seed_R8_openings_match_and_one_review_only_changes_its_own_run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var logins = await db.ProvisionDeployedLoginsAsync();
        await using var writer = NpgsqlDataSource.Create(logins.WorkerLedger);
        await using var host = TimelineHost(logins.Worker);
        var actor = TestActor.Staff($"staff:b3-{Guid.NewGuid():N}");
        var (service, store) = RunService(writer, host, actor);
        var first = await service.Create(new("42"), $"b3-a-{Guid.NewGuid():N}", ct);
        var second = await service.Create(new(first.Seed), $"b3-b-{Guid.NewGuid():N}", ct);
        var a = new ScenarioRunId(first.RunId);
        var b = new ScenarioRunId(second.RunId);
        Assert.NotEqual(a, b);
        var beforeA = await service.Snapshot(a, ct);
        var beforeB = await service.Snapshot(b, ct);
        Assert.Equal(beforeA.BoundaryMs, beforeB.BoundaryMs);
        Assert.Equal(beforeA.LastPosition, beforeB.LastPosition);
        Assert.NotEqual(beforeA.TimelineId, beforeB.TimelineId);

        var accountsA = await Accounts(a);
        var accountsB = await Accounts(b);
        var paymentsA = await Payments(a);
        var paymentsB = await Payments(b);
        Assert.Empty(accountsA.Select(x => x.Id).Intersect(accountsB.Select(x => x.Id)));
        Assert.Empty(paymentsA.Select(x => x.Id).Intersect(paymentsB.Select(x => x.Id)));
        Assert.Equal(12, accountsA.Length);
        Assert.Equal(4, paymentsA.Length);

        var openingA = await CanonicalOpening(a, first, beforeA, accountsA, paymentsA);
        var openingB = await CanonicalOpening(b, second, beforeB, accountsB, paymentsB);
        Assert.Equal(openingA, openingB);
        Assert.Equal(beforeA.Balances, beforeB.Balances);

        var directory = new RevenantRunDirectory(writer);
        var authorizer = new RevenantStaffAuthorizer(directory, directory);
        var memberOfA = TestActor.Staff($"staff:b3-member-{Guid.NewGuid():N}");
        Assert.True(await PaymentKit.Store(writer).GrantRunStaffAsync(a, memberOfA.SubjectId, "operator", ct));
        foreach (var operation in new[] { ResourceOperation.Read, ResourceOperation.Write })
        {
            Assert.True((await authorizer.AuthorizeAsync(actor, RevenantResources.Run(b), operation, ct)).IsAllowed);
            Assert.True((await authorizer.AuthorizeAsync(memberOfA, RevenantResources.Run(a), operation, ct)).IsAllowed);
            Assert.False((await authorizer.AuthorizeAsync(memberOfA, RevenantResources.Run(b), operation, ct)).IsAllowed);
        }

        var auditA = await service.Audit(a, ct);
        var auditB = await service.Audit(b, ct);
        var ledgerRowsB = await LedgerRows(b);
        var held = beforeA.Cases.First(c => c.Status == "Open" && c.RequiredApprovals == 1);
        var instruction = Assert.Single(beforeA.Payments, p => p.IntentId == held.IntentId);
        Assert.Equal("Held", instruction.Status);
        var accepted = await service.Review(a, held.IntentId, new("Approve", held.Version), $"b3-approve-{Guid.NewGuid():N}", ct);
        Assert.Equal("Released", accepted.Status);
        var afterA = await service.Snapshot(a, ct);
        var afterB = await service.Snapshot(b, ct);
        var auditAfterA = await service.Audit(a, ct);
        var auditAfterB = await service.Audit(b, ct);
        Assert.Equal(beforeA.BoundaryMs, afterA.BoundaryMs);
        Assert.Equal(beforeB.BoundaryMs, afterB.BoundaryMs);
        Assert.Equal("Settled", Assert.Single(afterA.Payments, p => p.IntentId == held.IntentId).Status);
        Assert.Equal(beforeB.LastPosition, afterB.LastPosition);
        Assert.Equal(JsonSerializer.Serialize(beforeB.Cases, Json), JsonSerializer.Serialize(afterB.Cases, Json));
        Assert.Equal(JsonSerializer.Serialize(beforeB.Payments, Json), JsonSerializer.Serialize(afterB.Payments, Json));
        Assert.Equal(beforeB.Balances, afterB.Balances);
        Assert.Equal(ledgerRowsB, await LedgerRows(b));
        Assert.Equal(JsonSerializer.Serialize(auditB, Json), JsonSerializer.Serialize(auditAfterB, Json));
        Assert.NotEqual(auditA.LedgerHeadHash, auditAfterA.LedgerHeadHash);
        Assert.Equal(new[] { "fact.reviewdecision", "fact.settlement" },
            auditAfterA.RecentEvents.Where(e => e.Sequence > auditA.RecentEvents[^1].Sequence).Select(e => e.Event));
        Assert.Equal("Verified", auditAfterA.LedgerVerification);
        Assert.Equal("Verified", auditAfterB.LedgerVerification);
        var principal = long.Parse(instruction.AmountMinor, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var before in beforeA.Balances)
        {
            var after = Assert.Single(afterA.Balances, x => x.BankId == before.BankId);
            var posted = before.BankId == instruction.SourceBank ? -principal : before.BankId == instruction.DestinationBank ? principal : 0;
            var reserved = before.BankId == instruction.SourceBank ? -principal : 0;
            Assert.Equal(posted, long.Parse(after.PostedMinor) - long.Parse(before.PostedMinor));
            Assert.Equal(reserved, long.Parse(after.ReservedMinor) - long.Parse(before.ReservedMinor));
            Assert.Equal(posted - reserved, long.Parse(after.AvailableMinor) - long.Parse(before.AvailableMinor));
        }
        output.WriteLine("B3_EQUAL_SEED " + JsonSerializer.Serialize(new
        {
            status = "PASS", first.Seed, openingDigestA = Digest(openingA), openingDigestB = Digest(openingB),
            banks = beforeA.Opening.Select(x => x.BankId), accounts = accountsA.Length, instructions = paymentsA.Length,
            runA = first.RunId, runB = second.RunId, timelineA = beforeA.TimelineId, timelineB = beforeB.TimelineId,
            accountIdsA = accountsA.Select(x => x.Id), accountIdsB = accountsB.Select(x => x.Id),
            paymentIdsA = paymentsA.Select(x => x.Id), paymentIdsB = paymentsB.Select(x => x.Id),
            boundaryMs = beforeA.BoundaryMs, intent = held.IntentId, paidRun = first.RunId,
            reviewStatusA = accepted.Status, paymentStatusA = "Settled",
            reviewStatusB = Assert.Single(afterB.Cases, c => c.IntentId == held.IntentId).Status,
            paymentStatusB = Assert.Single(afterB.Payments, p => p.IntentId == held.IntentId).Status,
            auditHeadA = auditAfterA.LedgerHeadHash, auditHeadB = auditAfterB.LedgerHeadHash,
            auditHeadBeforeA = auditA.LedgerHeadHash, auditHeadBeforeB = auditB.LedgerHeadHash,
        }, Json));

        async Task<string> CanonicalOpening(ScenarioRunId run, R8Run created, R8Snapshot snap,
            (Guid Id, string Semantic)[] accounts, (Guid Id, string Semantic)[] payments)
        {
            var history = await store.ReadFinanceHistoryAsync(Tenant, run, run, ct);
            var context = await store.ReadPinnedContextAsync(Tenant, run, ct);
            Assert.Equal(6, snap.Opening.Select(x => x.BankId).Distinct().Count());
            foreach (var bank in snap.Opening.Concat(snap.Balances))
                Assert.Equal(long.Parse(bank.AvailableMinor), long.Parse(bank.PostedMinor) - long.Parse(bank.ReservedMinor));
            var intents = history.Facts.Where(fact => fact.Input.Kind == ObservedFactKind.PaymentIntent).Select(fact =>
            {
                var intent = PaymentIntentCodec.Instance.Decode(fact.Input.Payload.AsSpan());
                Assert.Equal(run, intent.RunId);
                return new
                {
                    intent.IntentId, intent.Sequence, intent.SourceBankId, intent.SourceAccountId, intent.DestBankId, intent.DestAccountId,
                    intent.Amount, relativeMs = intent.EventTimeUtcMs - context.OriginMs, intent.Features, intent.Truth,
                };
            }).ToArray();
            Assert.Equal(payments.Length, intents.Length);
            return JsonSerializer.Serialize(new
            {
                created.Seed, created.ManifestHash, context.BasePolicy, context.ReviewPolicy, context.RiskModel,
                opening = snap.Opening.OrderBy(x => x.BankId, StringComparer.Ordinal),
                accounts = accounts.Select(x => x.Semantic), payments = payments.Select(x => x.Semantic), intents,
            }, Json);
        }

        Task<(Guid Id, string Semantic)[]> Accounts(ScenarioRunId run) => NamespaceRows(db.Owner, run, """
            SELECT a.id, jsonb_build_array(b.local_bank_id, a.local_account_id, a.currency, a.opening_balance_minor)::text
              FROM revenant.account a JOIN revenant.bank b ON b.id=a.bank_id AND b.scenario_run_id=a.scenario_run_id
             WHERE a.scenario_run_id=@run ORDER BY a.local_account_id COLLATE "C"
            """, ct);
        Task<(Guid Id, string Semantic)[]> Payments(ScenarioRunId run) => NamespaceRows(db.Owner, run, """
            SELECT p.id, jsonb_build_array(p.intent_id, src.local_account_id, dst.local_account_id, p.currency, p.amount_minor, truth.malicious)::text
              FROM revenant.payment p
              JOIN revenant.account src ON src.id=p.source_account_id AND src.scenario_run_id=p.scenario_run_id
              JOIN revenant.account dst ON dst.id=p.dest_account_id AND dst.scenario_run_id=p.scenario_run_id
              JOIN revenant.counterfactual_truth truth ON truth.source_run_id=p.scenario_run_id AND truth.intent_id=p.intent_id
             WHERE p.scenario_run_id=@run ORDER BY p.intent_id COLLATE "C"
            """, ct);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Overlapping_R8_submissions_cancel_durably_and_recover_in_a_fresh_worker_process()
    {
        var workerDll = Environment.GetEnvironmentVariable("REVENANT_R6_WORKER_DLL");
        Assert.True(File.Exists(workerDll), "Publish Hosts.Worker and set REVENANT_R6_WORKER_DLL.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var logins = await db.ProvisionDeployedLoginsAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var writerName = $"b3-writer-{suffix}";
        var workerName = $"b3-replay-{suffix}";
        await using var writer = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(logins.WorkerLedger)
        {
            ApplicationName = writerName,
        }.ConnectionString);
        await using var host = TimelineHost(logins.Worker);
        var actor = TestActor.Staff($"staff:b3-{suffix}");
        var (service, _) = RunService(writer, host, actor);
        var created = await service.Create(new("42"), $"b3-source-{suffix}", ct);
        var run = new ScenarioRunId(created.RunId);
        var source = await service.Close(run, $"b3-close-{suffix}", ct);
        var original = await service.Audit(run, ct);
        Assert.Equal("Verified", original.Status);
        Assert.Equal("Verified", original.LedgerVerification);
        var ledgerBefore = await LedgerRows(run);
        var fork = source.Markers.Where(m => m.Kind == "ObservationSeal" && m.AtMs <= source.Payments[0].AcceptedAtMs)
            .MaxBy(m => m.AtMs)!;
        var preview = await service.Preview(run, fork.Position, ct);
        Assert.True(long.Parse(fork.Position) < long.Parse(source.Markers.First(m => m.Kind == "PaymentIntent").Position));
        var request = new R8Replay(fork.Position, source.BoundaryMs, "policy-v8", null);
        await db.ApplyReplayContextPolicyAsync();
        var password = Guid.NewGuid().ToString("N");
        await db.ProvisionReplayLoginAsync(Tenant, password);
        var replayConnection = new NpgsqlConnectionStringBuilder(logins.Worker)
        {
            Username = "sands_replay", Password = password, ApplicationName = workerName,
        }.ConnectionString;

        var keyA = $"b3-same-{suffix}";
        var keyB = $"b3-distinct-{suffix}";
        await using var gate = await db.Owner.OpenConnectionAsync(ct);
        await using var submitTransaction = await gate.BeginTransactionAsync(ct);
        foreach (var key in new[] { keyA, keyB })
        {
            await using var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 8))", gate, submitTransaction);
            take.Parameters.AddWithValue("key", $"{Tenant}:{key}");
            await take.ExecuteNonQueryAsync(ct);
        }
        var sharedA = Submit(keyA);
        var sharedB = Submit(keyA);
        var distinct = Submit(keyB);
        await AwaitBlocked(writerName, "advisory", null, 3);
        await submitTransaction.CommitAsync(ct);
        var overlapping = await Task.WhenAll(sharedA, sharedB, distinct);
        var (a, submittedA, pendingA) = overlapping[0];
        var (b, submittedB, pendingB) = overlapping[2];
        Assert.Equal(a.JobId, overlapping[1].Job.JobId);
        Assert.NotEqual(a.JobId, b.JobId);
        Assert.All(overlapping, job =>
        {
            Assert.Equal("Pending", job.Job.Status);
            Assert.Null(job.Job.Verified);
        });
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.Replay(run, request with { HorizonMs = fork.AtMs + 300_000 }, keyA, ct));
        Assert.Equal("ConflictingOperationKey", conflict.Message);
        output.WriteLine($"B3_WORKER submitted {a.JobId:D} and {b.JobId:D} Pending; overlapping contenders=3; duplicate={overlapping[1].Job.JobId:D}");

        var (cancelled, _, _) = await Submit($"b3-cancel-pending-{suffix}");
        Assert.Equal("Pending", cancelled.Status);
        var cancelledRead = await service.Cancel(run, cancelled.JobId, $"b3-cancel-{suffix}", ct);
        Assert.Equal("Cancelled", cancelledRead.Status);
        Assert.Null(cancelledRead.Verified);
        Assert.Equal(0L, await Children(cancelled.JobId));
        Assert.Equal(2, await LaunchToExit(cancelled.JobId));
        Assert.Equal(cancelledRead, await service.Job(run, cancelled.JobId, ct));
        Assert.Equal(0L, await Children(cancelled.JobId));
        output.WriteLine($"B3_WORKER {cancelled.JobId:D} Pending -> Cancelled; fresh process skipped; child count=0");

        var (third, submittedThird, pendingThird) = await Submit($"b3-third-{suffix}");
        var timing = new ConcurrentQueue<object>();
        var evidence = new List<object>();
        var childIds = new HashSet<Guid>();
        var firstTwo = await Task.WhenAll(ObserveWorker(a.JobId, submittedA, pendingA), ObserveWorker(b.JobId, submittedB, pendingB));
        await Record(firstTwo[0]);
        await Record(firstTwo[1]);
        await Record(await ObserveWorker(third.JobId, submittedThird, pendingThird));

        // The worker has durably entered Running and is waiting for its first journal write.
        // A separate executed proxy test covers the lost acknowledgment after a committed write.
        var (interrupted, _, _) = await Submit($"b3-interrupt-{suffix}");
        await using (var checkpoint = await BlockAppend())
        {
            using var process = Launch(interrupted.JobId);
            try
            {
                await AwaitBlocked(workerName, "relation", "marrow_timeline.steps", 1);
                var running = await service.Job(run, interrupted.JobId, ct);
                Assert.Equal("Running", running.Status);
                Assert.Null(running.Verified);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct);
                Assert.Equal(running, await service.Job(run, interrupted.JobId, ct));
                output.WriteLine($"B3_WORKER {interrupted.JobId:D} Running; interrupted PID {process.Id} at journal write lock; durable result absent");
            }
            finally
            {
                await Stop(process);
            }
        }
        Assert.Equal(0, await LaunchToExit(interrupted.JobId));
        await Record(await ReadTerminal(interrupted.JobId), recovered: true);

        // Hold an executing worker at the same write checkpoint while the authorized actor cancels.
        var (runningCancel, _, _) = await Submit($"b3-cancel-running-{suffix}");
        int runningCancelExit;
        await using (var checkpoint = await BlockAppend())
        using (var process = Launch(runningCancel.JobId))
        {
            try
            {
                await AwaitBlocked(workerName, "relation", "marrow_timeline.steps", 1);
                var running = await service.Job(run, runningCancel.JobId, ct);
                Assert.Equal("Running", running.Status);
                Assert.Null(running.Verified);
                var result = await service.Cancel(run, runningCancel.JobId, $"b3-cancel-running-actor-{suffix}", ct);
                Assert.Equal("Cancelled", result.Status);
                Assert.Null(result.Verified);
                await checkpoint.RollbackAsync(ct);
                await process.WaitForExitAsync(ct);
                runningCancelExit = process.ExitCode;
                output.WriteLine($"B3_WORKER {runningCancel.JobId:D} Running -> Cancelled; process exit={runningCancelExit}");
            }
            finally { await Stop(process); }
        }
        var runningCancelledChildren = await Children(runningCancel.JobId);
        var afterCancel = await service.Job(run, runningCancel.JobId, ct);
        Assert.Equal("Cancelled", afterCancel.Status);
        Assert.Null(afterCancel.Verified);
        var retry = await service.Replay(run, request, keyA, ct);
        Assert.Equal(a.JobId, retry.JobId);
        Assert.Equal("Verified", retry.Status);
        Assert.Equal(1L, await Children(a.JobId));
        Assert.Equal(2, await LaunchToExit(a.JobId));
        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(await service.Audit(run, ct), Json));
        Assert.Equal(ledgerBefore, await LedgerRows(run));

        output.WriteLine("B3_REPLAY " + JsonSerializer.Serialize(new
        {
            runId = run.Value, source.TimelineId, source.LastPosition, fork, source.BoundaryMs,
            original.RecordHash, original.StateHash, original.LedgerHeadHash, original.Model, request.Policy,
            paymentInstructions = source.Payments.Length, sourceSeals = source.Markers.Count(m => m.Kind == "ObservationSeal"),
            pendingCancelled = cancelled.JobId, cancelledWhileRunning = runningCancel.JobId, runningCancelledChildren, runningCancelExit, interrupted = interrupted.JobId,
            environment = new
            {
                platform = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(),
                harnessRuntime = RuntimeInformation.FrameworkDescription, workerBuild = "published Release, one-shot worker process",
                runnerImage = Environment.GetEnvironmentVariable("ImageOS"),
            },
            children = evidence, timing,
        }, Json));
        Assert.Equal(2, runningCancelExit);

        async Task<(R8Job Job, long Submitted, double PendingMs)> Submit(string key)
        {
            var started = Stopwatch.GetTimestamp();
            var job = await service.Replay(run, request, key, ct);
            return (job, started, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        async Task AwaitBlocked(string application, string type, string? relation, long expected)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(TimeSpan.FromSeconds(20));
            await using var query = db.Owner.CreateCommand("""
                SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid
                 WHERE NOT l.granted AND a.application_name=@app AND l.locktype=@kind
                  AND (@rel IS NULL OR l.relation=to_regclass(@rel))
                """);
            query.Parameters.AddWithValue("app", application);
            query.Parameters.AddWithValue("kind", type);
            query.Parameters.AddWithValue("rel", NpgsqlTypes.NpgsqlDbType.Text, (object?)relation ?? DBNull.Value);
            while ((long)(await query.ExecuteScalarAsync(bound.Token))! != expected)
                await Task.Delay(25, bound.Token);
        }

        async Task<NpgsqlTransaction> BlockAppend()
        {
            var tx = await gate.BeginTransactionAsync(ct);
            try
            {
                await using var block = new NpgsqlCommand("LOCK TABLE marrow_timeline.steps IN SHARE MODE", gate, tx);
                await block.ExecuteNonQueryAsync(ct);
                return tx;
            }
            catch
            {
                await tx.DisposeAsync();
                throw;
            }
        }

        Process Launch(Guid job)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(workerDll!);
            start.ArgumentList.Add("--replay");
            start.ArgumentList.Add(job.ToString("D"));
            start.Environment["ConnectionStrings__RevenantReplay"] = replayConnection;
            start.Environment["Revenant__Ledger__TenantId"] = Tenant;
            start.Environment.Remove("ConnectionStrings__RevenantLedger");
            var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start test Replay Worker.");
            // Retain only the exception type and SQLSTATE. Exception messages can carry connection details.
            process.ErrorDataReceived += (_, line) =>
            {
                var failure = Regex.Match(line.Data ?? "", @"^Unhandled exception\. ([\w.]+Exception)(?: \(0x[0-9A-Fa-f]+\))?(?:: ([A-Z0-9]{5}):)?");
                if (failure.Success)
                    output.WriteLine($"B3_WORKER {job:D} exception={failure.Groups[1].Value} sqlstate={failure.Groups[2].Value}");
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        async Task<int> LaunchToExit(Guid job)
        {
            using var process = Launch(job);
            try
            {
                await process.WaitForExitAsync(ct);
                return process.ExitCode;
            }
            finally { await Stop(process); }
        }

        async Task<R8Job> ObserveWorker(Guid job, long submitted, double pendingMs)
        {
            var launch = Stopwatch.GetTimestamp();
            using var process = Launch(job);
            double? observedRunning = null;
            try
            {
                R8Job current;
                do
                {
                    current = await service.Job(run, job, ct);
                    if (current.Status is "Pending" or "Running") Assert.Null(current.Verified);
                    if (current.Status == "Running")
                        observedRunning ??= Stopwatch.GetElapsedTime(launch).TotalMilliseconds;
                    if (current.Status is "Pending" or "Running") await Task.Delay(25, ct);
                }
                while ((current.Status is "Pending" or "Running") && !process.HasExited);
                // A final read covers a publication between the preceding read and process exit.
                if (current.Status is "Pending" or "Running") current = await service.Job(run, job, ct);
                var terminal = Stopwatch.GetTimestamp();
                await process.WaitForExitAsync(ct);
                Assert.True(process.ExitCode == 0, $"Replay process exit {process.ExitCode}, job {job:D}, {current.Status}, {current.Failure}");
                Assert.Equal("Verified", current.Status);
                timing.Enqueue(new
                {
                    jobId = job, submitToPendingMs = pendingMs, syntheticQueueToProcessLaunchMs = Stopwatch.GetElapsedTime(submitted, launch).TotalMilliseconds - pendingMs,
                    processLaunchToObservedRunningMs = observedRunning,
                    observedRunningToVerifiedMs = observedRunning is { } start ? Stopwatch.GetElapsedTime(launch, terminal).TotalMilliseconds - start : (double?)null,
                    processLaunchToVerifiedMs = Stopwatch.GetElapsedTime(launch, terminal).TotalMilliseconds,
                    submitToVerifiedMs = Stopwatch.GetElapsedTime(submitted, terminal).TotalMilliseconds,
                });
                output.WriteLine($"B3_WORKER {job:D} observed Running={observedRunning is not null}; Verified; exit=0");
                return current;
            }
            finally { await Stop(process); }
        }

        async Task<R8Job> ReadTerminal(Guid job)
        {
            var terminal = await service.Job(run, job, ct);
            Assert.Equal("Verified", terminal.Status);
            return terminal;
        }

        Task<long> Children(Guid job) => PaymentKit.Scalar<long>(db.Owner, """
            SELECT count(*) FROM marrow_timeline.histories WHERE parent_timeline_id=@source AND operation_key=@op
            """, ("source", source.TimelineId), ("op", $"r8:{job:N}"));

        async Task Record(R8Job job, bool recovered = false)
        {
            var verified = Assert.IsType<R8Verified>(job.Verified);
            Assert.True(childIds.Add(verified.ChildTimelineId));
            Assert.Equal(original.RecordHash, verified.SourceRecordHash);
            Assert.Equal(original.StateHash, verified.SourceStateHash);
            Assert.Equal(4, verified.Headlines.Length);
            Assert.Equal(1L, await Children(job.JobId));
            await using var query = db.Owner.CreateCommand("""
                SELECT h.parent_timeline_id, h.fork_position, h.base_state_hash, h.last_record_hash, h.latest_state_hash, h.status,
                       h.last_position, count(s.position), min(s.position), max(s.position)
                  FROM marrow_timeline.histories h LEFT JOIN marrow_timeline.steps s ON s.timeline_id=h.timeline_id
                 WHERE h.timeline_id=@child GROUP BY h.timeline_id
                """);
            query.Parameters.AddWithValue("child", verified.ChildTimelineId);
            await using var row = await query.ExecuteReaderAsync(ct);
            Assert.True(await row.ReadAsync(ct));
            Assert.Equal(source.TimelineId, row.GetGuid(0));
            var position = row.GetInt64(1);
            Assert.Equal(fork.Position, position.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(preview.StateHash, row.GetString(2));
            Assert.Equal(verified.ChildRecordHash, row.GetString(3));
            Assert.Equal(verified.ChildStateHash, row.GetString(4));
            Assert.Equal("Verified", row.GetString(5));
            var childLast = row.GetInt64(6);
            var suffixCount = row.GetInt64(7);
            Assert.Equal(childLast - position, suffixCount);
            Assert.Equal(position + 1, row.GetInt64(8));
            Assert.Equal(childLast, row.GetInt64(9));
            evidence.Add(new
            {
                jobId = job.JobId, recovered, verified.ChildTimelineId, verified.ChildRecordHash, verified.ChildStateHash,
                childLast, suffixCount, verified.Headlines,
            });
            if (recovered) output.WriteLine($"B3_WORKER {job.JobId:D} fresh process recovered Verified; unique suffix steps={suffixCount}");
        }
    }

    private static async Task Stop(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static string Digest(string semantic) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(semantic)));

    private static ServiceProvider TimelineHost(string connection)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        return services.BuildServiceProvider();
    }

    private static (R8Service Service, V4SimulationStore Store) RunService(NpgsqlDataSource writer, ServiceProvider host, TestActor actor)
    {
        var ledger = PaymentKit.Store(writer);
        var model = new Lazy<Result<BehavioralRiskScorerV2, BehavioralRiskFailureV2>>(() =>
            BehavioralRiskScorerV2.Load(Path.Combine(AppContext.BaseDirectory, "Intelligence", "Artifacts",
                BehavioralRiskScorerV2.ArtifactDirectory), BehavioralRiskScorerV2.FeatureSchema, BehavioralRiskScorerV2.PublishedPin));
        var store = new V4SimulationStore(writer, ledger: ledger, model: model);
        var engine = host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
        return (new R8Service(writer, store, new V4BranchExecution(store, engine), engine, ledger, actor), store);
    }

    private async Task<string> LedgerRows(ScenarioRunId run) => await PaymentKit.Scalar<string>(db.Owner, """
        SELECT jsonb_build_array(
            (SELECT jsonb_agg(to_jsonb(e) ORDER BY e.id) FROM revenant.ledger_entry e WHERE e.scenario_run_id=@run),
            (SELECT jsonb_agg(to_jsonb(h) ORDER BY h.id) FROM revenant.hold h WHERE h.scenario_run_id=@run)
        )::text
        """, ("run", run.Value));

    private static async Task<(Guid Id, string Semantic)[]> NamespaceRows(NpgsqlDataSource owner, ScenarioRunId run, string sql, CancellationToken ct)
    {
        await using var query = owner.CreateCommand(sql);
        query.Parameters.AddWithValue("run", run.Value);
        await using var reader = await query.ExecuteReaderAsync(ct);
        var rows = new List<(Guid, string)>();
        while (await reader.ReadAsync(ct)) rows.Add((reader.GetGuid(0), reader.GetString(1)));
        return rows.ToArray();
    }

}
