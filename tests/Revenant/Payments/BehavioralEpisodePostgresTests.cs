using Marrow.Infrastructure.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Revenant.Cases;
using Revenant.Contracts;
using Revenant.Finance;
using Revenant.Observations;
using Revenant.Payments;
using Revenant.Scenario;
using Revenant.Tests.Ledger;
using Revenant.Tests.Scenario;
using Revenant.Timeline;
using Xunit;
using Xunit.Abstractions;
using OpeningFixture = Revenant.Tests.Scenario.BehavioralEpisodeScenarioTests;

namespace Revenant.Tests.Payments;

[Collection(PaymentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BehavioralEpisodePostgresTests(LedgerDatabase db, ITestOutputHelper output)
{
    [Fact]
    public async Task Six_bank_behavioral_root_commits_real_payments_holds_returns_and_seals()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        const string tenant = PaymentKit.TenantA;
        const string owner = PaymentKit.Operator;
        var id = OpeningFixture.RunId;
        var run = OpeningFixture.Opening(id);
        var context = OpeningFixture.Pin(run);
        var origin = context.OriginMs;
        var firstBoundary = origin + ObservedFinanceVersion.BucketMs;
        var until = origin + OpeningFixture.Duration;
        var ledger = PaymentKit.Store(db.Writer);
        var store = new V4SimulationStore(db.Writer, ledger: ledger);
        var logins = await db.ProvisionDeployedLoginsAsync();
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(logins.Worker));
        services.AddMarrowTimeline(ObservedFinanceTimelineVersions.All, ObservedFinanceTimelineVersions.CheckpointInterval);
        await using var host = services.BuildServiceProvider();
        var engine = host.GetRequiredService<ITimelineEngine<ObservedFinanceState, ObservedFinanceInput, ObservedFinanceContext>>();
        var execution = new V4BranchExecution(store, engine);
        var binding = await execution.OpenSourceAsync(tenant, owner, run, context, ct);
        Assert.Equal(id, binding.ExecutionRunId);
        Assert.Equal(id, binding.LineageRunId);
        Assert.Equal(run.Manifest.ContentHash, (await store.ReadPinnedContextAsync(tenant, id, ct)).ManifestSha256);
        Assert.Equal(6, await PaymentKit.Count(db.Owner, "bank", id.Value));
        Assert.Equal(18, await PaymentKit.Count(db.Owner, "account", id.Value));
        var expected = run.Manifest.Accounts.ToDictionary(a => a.AccountId, a => (a.BankId, Posted: a.OpeningBalance.MinorUnits, Reserved: 0L));
        await VerifyBalances();

        var accounts = run.Manifest.Accounts;
        var timing = SeededRandom.ForStream(run.Manifest.Seed, 0x52563402);
        var transfer = Intent("cross-bank", 1, origin + timing.NextInt(1, 60_000), accounts[0], accounts[2], 61_011);
        await Execute(Intake(transfer), nameof(PaymentStatus.Initiated));
        await Execute(Opportunity(transfer, "Screening"), nameof(PaymentStatus.Cleared));
        await Execute(Opportunity(transfer, "Settlement"), nameof(PaymentStatus.Settled));
        Move(transfer.SourceAccountId, transfer.DestAccountId, transfer.Amount.MinorUnits);
        await VerifyBalances();

        var held = Intent("held", 2, firstBoundary, accounts[0], accounts[3], 87_001, sanctionsHit: true);
        _ = await store.AdmitAsync(tenant, id, Intake(held), ct);
        var seal = Assert.IsType<ObservedFinanceInput>((await store.AdvanceOneStepAsync(tenant, id, ct)).Seal);
        Assert.Equal(firstBoundary, seal.EffectiveTimeMs);
        Assert.Equal(4, seal.Sequence);
        Assert.Equal(origin, ObservationSealCodec.Decode(seal.Payload.AsSpan()).IntervalStartMs);
        Assert.Null(await ledger.GetPaymentAsync(tenant, id, held.IntentId, ct));
        var streaming = V4ObservationProjector.Project(await execution.ReadProjectionHistoryAsync(tenant, id, ct));
        Assert.Equal(6, streaming.Banks.Count);
        Assert.Null(await store.ReadBankAsync(tenant, id, transfer.SourceBankId, firstBoundary, ct));
        await store.MaterializeBanksAsync(tenant, id, streaming.Banks, ct);
        await Execute(Intake(held), nameof(PaymentStatus.Initiated));
        await Execute(Opportunity(held, "Screening"), nameof(PaymentStatus.Held));
        var payer = expected[held.SourceAccountId];
        expected[held.SourceAccountId] = (payer.BankId, payer.Posted, held.Amount.MinorUnits);
        Assert.NotNull(await ledger.GetCaseAsync(tenant, id, held.IntentId, ct));
        await VerifyBalances();

        var original = (await ledger.GetPaymentAsync(tenant, id, transfer.IntentId, ct))!;
        var returnTime = firstBoundary + 1;
        await Execute(new("return-cross-bank", V4CommandClass.ConditionalHumanRequest, "ReturnInstruction", "v4-human-request/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(transfer.IntentId).WriteString(owner)
                .WriteInt64(original.Version).WriteBoolean(true).WriteByte((byte)CaseAction.Assign).ToArray()],
            returnTime, returnTime), nameof(PaymentStatus.Returned));
        Move(transfer.DestAccountId, transfer.SourceAccountId, transfer.Amount.MinorUnits);

        var sameBank = Intent("same-bank", 3, returnTime, accounts[0], accounts[1], 12_301);
        await Execute(Intake(sameBank), nameof(PaymentStatus.Initiated));
        await Execute(Opportunity(sameBank, "Screening"), nameof(PaymentStatus.Cleared));
        await Execute(Opportunity(sameBank, "Settlement"), nameof(PaymentStatus.Settled));
        Move(sameBank.SourceAccountId, sameBank.DestAccountId, sameBank.Amount.MinorUnits);

        var rejected = Intent("insufficient", 4, returnTime, accounts[0], accounts[3], 10_000_000, sanctionsHit: true);
        await Execute(Intake(rejected), nameof(PaymentStatus.Initiated));
        await Execute(Opportunity(rejected, "Screening"), nameof(PaymentStatus.Rejected));
        await VerifyBalances();
        Assert.Equal(3, await PaymentKit.Count(db.Owner, "ledger_group", id.Value));
        Assert.Equal(1, await PaymentKit.Count(db.Owner, "hold", id.Value));

        for (var boundary = firstBoundary + ObservedFinanceVersion.BucketMs; boundary <= until; boundary += ObservedFinanceVersion.BucketMs)
        {
            var step = await store.SealAsync(tenant, id, boundary, ct);
            Assert.Equal(V4StepKind.Sealed, step.Kind);
            Assert.Equal(boundary, step.Seal!.EffectiveTimeMs);
        }

        var journal = await store.ReadJournalAsync(tenant, id, ct);
        Assert.Equal(11, journal.Count);
        Assert.All(journal, command => Assert.Equal(V4CommandStatus.Completed, command.Status));
        var final = await execution.CompleteAsync(tenant, id, ct);
        Assert.Equal(HistoryStatus.Verified, final.Status);
        Assert.Equal(35, final.FinalPosition);
        var replay = (await engine.ReplayAsync(binding.TimelineId, ct)).Value;
        Assert.Equal(final.FinalStateHash, replay.FinalStateHash);
        Assert.Equal(until, replay.FinalState.LastSealedBoundaryMs);
        Assert.Equal(run.Manifest.Accounts.Sum(a => a.OpeningBalance.MinorUnits), replay.FinalState.Ledger.TotalPostedMinor);
        Assert.All(replay.FinalState.Ledger.Payments.Values, p => Assert.Equal(SyntheticTruth.Legitimate, p.Intent.Truth));
        var history = await execution.ReadProjectionHistoryAsync(tenant, id, ct);
        var offline = V4ObservationProjector.Project(history);
        Assert.Equal(144, offline.Banks.Count);
        Assert.All(offline.Banks.GroupBy(row => row.Seal.BoundaryMs), bucket =>
        {
            Assert.Equal(6, bucket.Count());
            Assert.Equal(0, bucket.Sum(row => row.NetSettled));
            Assert.Equal(0, bucket.Sum(row => row.ReturnedIn - row.ReturnedOut));
        });
        Assert.Equal(streaming.Banks.Select(row => row.Digest), offline.Banks.Take(6).Select(row => row.Digest));
        var firstBank = offline.Banks[0];
        var secondBank = offline.Banks[6];
        Assert.Equal((1L, 0L, transfer.Amount.MinorUnits, 0L),
            (firstBank.Cleared, firstBank.Held, firstBank.SettledOut, firstBank.Balance.Reserved));
        Assert.Equal((1L, 1L, sameBank.Amount.MinorUnits, sameBank.Amount.MinorUnits, transfer.Amount.MinorUnits, held.Amount.MinorUnits),
            (secondBank.Held, secondBank.Rejected, secondBank.SettledOut, secondBank.SettledIn, secondBank.ReturnedIn, secondBank.Reserved));
        Assert.Equal(history.Segments[0].Facts[3].PrefixDigest, firstBank.Seal.PrefixDigest);
        await store.MaterializeBanksAsync(tenant, id, offline.Banks, ct);
        await new V4SimulationStore(db.Writer).MaterializeBanksAsync(tenant, id, offline.Banks, ct);
        Assert.Equal(144, await PaymentKit.Count(db.Owner, "v4_bank_observation", id.Value, "execution_run_id"));
        var readOnly = new V4SimulationStore(db.Replay);
        Assert.Equal(firstBank, await readOnly.ReadBankAsync(tenant, id, firstBank.BankId, firstBoundary, ct));
        Assert.Null(await readOnly.ReadBankAsync(PaymentKit.TenantB, id, firstBank.BankId, firstBoundary, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MaterializeBanksAsync(tenant, id, [firstBank with { Intents = 2 }], ct));
        Assert.Equal(firstBank, await readOnly.ReadBankAsync(tenant, id, firstBank.BankId, firstBoundary, ct));
        output.WriteLine($"Manifest {context.ManifestSha256}; 6 banks; 18 accounts; 2h; 4 intakes; 4 screenings; 2 settlements; 1 hold; 1 rejection; 1 return; 24 seals; final prefix {replay.FinalState.PrefixDigest}");
        output.WriteLine($"144 bank observations; first {firstBank.Digest}; last {offline.Banks[^1].Digest}");

        PaymentIntent Intent(string key, long seq, long at, AccountDescriptor source, AccountDescriptor dest, long minor, bool sanctionsHit = false) =>
            new(id, key, seq, at, source.BankId, source.AccountId, dest.BankId, dest.AccountId, Money.Usd(minor),
                SyntheticTruth.Legitimate, new(true, sanctionsHit));

        async Task Execute(V4CommandRequest request, string outcome)
        {
            _ = await store.AdmitAsync(tenant, id, request, ct);
            var step = await store.AdvanceOneStepAsync(tenant, id, ct);
            Assert.Equal(V4StepKind.ProcessedCommand, step.Kind);
            Assert.Equal(V4CommandStatus.Completed, step.Command!.Status);
            Assert.Equal(request.Key, step.Command.Request.Key);
            Assert.Equal(outcome, step.Command.OutcomeCode);
        }

        void Move(string from, string to, long minor)
        {
            var a = expected[from];
            var b = expected[to];
            expected[from] = (a.BankId, checked(a.Posted - minor), a.Reserved);
            expected[to] = (b.BankId, checked(b.Posted + minor), b.Reserved);
        }

        async Task VerifyBalances()
        {
            await using var query = db.Owner.CreateCommand("""
                SELECT a.local_account_id, b.local_bank_id, a.posted_minor, a.reserved_minor, a.available_minor
                FROM revenant.account_available_balance a JOIN revenant.bank b ON b.id = a.bank_id
                WHERE a.scenario_run_id = @run
                """);
            query.Parameters.AddWithValue("run", id.Value);
            await using var rows = await query.ExecuteReaderAsync(ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await rows.ReadAsync(ct))
            {
                var account = rows.GetString(0);
                Assert.True(seen.Add(account));
                var (bank, posted, reserved) = expected[account];
                Assert.Equal((bank, posted, reserved), (rows.GetString(1), rows.GetInt64(2), rows.GetInt64(3)));
                Assert.Equal(checked(posted - reserved), rows.GetInt64(4));
                Assert.True(rows.GetInt64(4) >= 0);
            }
            Assert.Equal(expected.Count, seen.Count);
        }
    }

    private static V4CommandRequest Intake(PaymentIntent p) =>
        new($"intake-{p.IntentId}", V4CommandClass.Exogenous, "PaymentIntent", ScenarioSchemas.InputSchemaVersion,
            [.. PaymentIntentCodec.Instance.EncodeCanonical(p)], p.EventTimeUtcMs, p.EventTimeUtcMs);

    private static V4CommandRequest Opportunity(PaymentIntent p, string kind) =>
        new($"{kind}-{p.IntentId}", V4CommandClass.DerivedOpportunity, kind, "v4-payment-opportunity/1",
            [.. new CanonicalWriter().WriteInt32(1).WriteString(p.IntentId).ToArray()], p.EventTimeUtcMs, p.EventTimeUtcMs);
}
