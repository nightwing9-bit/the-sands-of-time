extern alias realtime;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Multiplayer;
using Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using realtime::GhostLapHost;
using Xunit;

namespace Integration.Tests.GhostLap;

public sealed partial class AuthorityAbortVerificationTests
{
    [Fact]
    public async Task Four_match_capacity_includes_pending_commits_and_releases_after_abort_and_completion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        await using var host = new RealtimeHostFactory(db, IdentityApiFactory.NewMasterSecret());
        var authority = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        var store = host.Services.GetRequiredService<MatchAuthorityStore>();
        var tickTime = new ManualClock();
        var roomTime = new ManualClock();
        var options = new MultiplayerOptions { MaxConcurrentMatches = 4 };
        var registry = new MatchRegistry(options, tickTime, authority, store, (delay, _) =>
        {
            tickTime.Advance(delay);
            return Task.CompletedTask;
        });
        try
        {
            var owner = new ActiveRoomOwner(options, roomTime, registry, registry, new CryptoRoomRandom(), authority.GenerationId, registry);
            var (caller, joined, cancelledRound) = BeginCountdown(owner);
            Assert.Equal(RoomPhases.Lobby, Value(owner.CancelCountdown(caller, new("cancel", joined.View.RoomId))).View.Phase);
            roomTime.Advance(TimeSpan.FromMilliseconds(options.CountdownMilliseconds + 1));
            await owner.SweepAsync(ct);
            Assert.Equal(0, registry.ReservedStarts);
            Assert.Null(await ReceiptSnapshotAsync(Guid.Parse(cancelledRound.MatchId)));

            var admitted = Enumerable.Range(0, 4).Select(_ => Seal(authority.GenerationId)).ToArray();
            var fifth = Seal(authority.GenerationId);
            await using (var gate = await PauseAtAsync(
                "capacity", "AFTER INSERT ON ghostlap.match_session", $"NEW.match_id = '{admitted[0].MatchId:D}'", 108, ct))
            {
                using var cancelWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var first = registry.AdmitAsync(admitted[0], cancelWait.Token).AsTask();
                await WaitBlockedAsync(GhostLapMatchDataSource.ApplicationName, gate.BackendPid, ct);
                var rest = admitted.Skip(1).Select(config => registry.AdmitAsync(config, ct).AsTask()).ToArray();
                await UntilAsync(() => Task.FromResult(registry.CaptureLoad().Capacity is var cap && cap.LiveMatches + cap.ReservedStarts == 4),
                    "Four reservations or live matches", ct);

                await cancelWait.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                Assert.True(registry.ReservedStarts > 0); // the database outcome is still unknown
                Assert.Equal(StartAdmissionOutcome.Unavailable, await registry.AdmitAsync(fifth, ct));
                Assert.Null(await ReceiptSnapshotAsync(fifth.MatchId));

                var retry = registry.AdmitAsync(admitted[0], ct).AsTask();
                await gate.ReleaseAsync(ct);
                Assert.Equal(StartAdmissionOutcome.Admitted, await retry);
                Assert.All(await Task.WhenAll(rest), outcome => Assert.Equal(StartAdmissionOutcome.Admitted, outcome));
            }

            Assert.Equal(4, registry.Matches.Count);
            Assert.Equal(0, registry.ReservedStarts);
            Assert.Equal(1, registry.StartsDeniedForCapacity);
            foreach (var config in admitted)
            {
                await AssertReceiptAsync(config, "absent", "Started:0:-");
            }

            registry.Orphaned(admitted[0].MatchId);
            await UntilAsync(() => Task.FromResult(registry.CaptureLoad().Capacity.LiveMatches == 3), "Abort released one place", ct);
            Assert.Equal(MatchStatuses.Aborted, registry.StatusOf(admitted[0].MatchId));
            Assert.Equal(StartAdmissionOutcome.Admitted, await registry.AdmitAsync(fifth, ct));

            registry.Activate(admitted[1].MatchId, [.. admitted[1].Slots.Select(s => new SlotControl(s.Slot, 1, s.MemberId))]);
            var runtime = Assert.IsType<MatchRuntime>(registry.Find(admitted[1].MatchId));
            await UntilAsync(() => Task.FromResult(registry.CaptureLoad().Capacity.LiveMatches == 3), "Completion released one place", ct);
            Assert.Equal(MatchStatuses.Finalized, runtime.Status);
            Assert.Equal(2700, runtime.PublishedTicks);
            Assert.Equal(3, registry.CaptureLoad().Capacity.LiveMatches);
            Assert.Equal(StartAdmissionOutcome.Admitted, await registry.AdmitAsync(Seal(authority.GenerationId), ct));
        }
        finally
        {
            await registry.StopAllAsync(ct);
        }
    }

    [Fact]
    public async Task Inputs_after_the_last_drain_and_during_result_commit_are_refused()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var host = new RealtimeHostFactory(db, IdentityApiFactory.NewMasterSecret());
        var authority = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        var store = host.Services.GetRequiredService<MatchAuthorityStore>();
        var time = new FinalTickClock();
        var registry = new MatchRegistry(new MultiplayerOptions(), time, authority, store, (delay, _) =>
        {
            time.Advance(delay);
            return Task.CompletedTask;
        });
        try
        {
            var round = Seal(authority.GenerationId);
            Assert.Equal(StartAdmissionOutcome.Admitted, await registry.AdmitAsync(round, ct));
            var runtime = Assert.IsType<MatchRuntime>(registry.Find(round.MatchId));
            time.Runtime = runtime;
            registry.Activate(round.MatchId, [.. round.Slots.Select(s => new SlotControl(s.Slot, 1, s.MemberId))]);
            await time.FinalTickPublished.Task.WaitAsync(ct);

            // Hold the driver's post-publication timing read. Tick 2700 has drained the mailbox and no subsequent tick can drain it.
            Assert.Equal(2700, runtime.Engine.State.Tick);
            Assert.Equal(MatchStatuses.Running, runtime.Status);
            var slot = round.Slots[0];
            var item = new GameplayInput(round.MatchId.ToString("N"), 1, 2699, 1, 1, 0, true);
            var late = new AdmittedInput(round.RoomId, slot.MemberId, slot.Subject, 1, item, slot.MemberId);
            var hash = MpStateCodec.Hash(runtime.Engine.State);
            Assert.Equal(GhostLapFailure.NoRunningMatch, registry.Submit(late));
            Assert.Equal(0, runtime.Engine.QueuedInputs);
            await using (var gate = await PauseAtAsync(
                "terminal_ingress", "AFTER INSERT ON ghostlap.match_result", $"NEW.match_id = '{round.MatchId:D}'", 110, ct))
            {
                time.Release.Set();
                await WaitBlockedAsync(GhostLapMatchDataSource.ApplicationName, gate.BackendPid, ct);
                Assert.Equal(MatchStatuses.PendingFinalization, runtime.Status);
                Assert.Equal(GhostLapFailure.NoRunningMatch, registry.Submit(late));
                await gate.ReleaseAsync(ct);
            }
            await UntilAsync(() => Task.FromResult(runtime.Status == MatchStatuses.Finalized), "The result still commits", ct);
            Assert.Equal(0, runtime.Engine.QueuedInputs);
            Assert.Equal(hash, runtime.Result!.Document.FinalStateHash);
            Assert.Equal(0, runtime.Engine.TotalAccepted);
            Assert.Equal(0, runtime.Engine.TotalRejected);
            Assert.Equal(GhostLapFailure.NoRunningMatch,
                registry.Submit(new AdmittedInput(round.RoomId, slot.MemberId, slot.Subject, 1, item, slot.MemberId)));
        }
        finally
        {
            time.Release.Set();
            await registry.StopAllAsync(ct);
            time.Release.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_filled_mailbox_is_explicitly_settled_on_overload_or_guard_loss(bool guardLoss)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var host = new RealtimeHostFactory(db, IdentityApiFactory.NewMasterSecret());
        var active = host.Services.GetRequiredService<GhostLapRealtimeAuthority>();
        var authority = new TestAuthority(active.GenerationId);
        var store = host.Services.GetRequiredService<MatchAuthorityStore>();
        var time = new ManualClock();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new MatchRegistry(new MultiplayerOptions { MailboxCapacity = 16 }, time, authority, store, (_, token) =>
        {
            parked.TrySetResult();
            return release.Task.WaitAsync(token);
        });
        try
        {
            var round = Seal(authority.GenerationId);
            Assert.Equal(StartAdmissionOutcome.Admitted, await registry.AdmitAsync(round, ct));
            registry.Activate(round.MatchId, [.. round.Slots.Select(s => new SlotControl(s.Slot, 1, s.MemberId))]);
            var runtime = Assert.IsType<MatchRuntime>(registry.Find(round.MatchId));
            await parked.Task.WaitAsync(ct);
            var slot = round.Slots[0];
            AdmittedInput Item(int seq) => new(round.RoomId, slot.MemberId, slot.Subject, 1,
                new GameplayInput(round.MatchId.ToString("N"), 1, 1, seq, 1, 0, true), slot.MemberId);
            for (var seq = 1; seq <= 16; seq++)
            {
                Assert.Null(registry.Submit(Item(seq)));
            }
            Assert.Equal(GhostLapFailure.MailboxFull, registry.Submit(Item(17)));
            if (guardLoss)
            {
                authority.Lose();
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(6));
            }
            release.SetResult();
            await UntilAsync(() => Task.FromResult(runtime.Status == MatchStatuses.Aborted), "The abort commits", ct);
            Assert.Equal(guardLoss ? MatchStopReason.AuthorityLost : MatchStopReason.Overloaded, runtime.StopReason);
            Assert.Equal(0, runtime.Engine.QueuedInputs);
            Assert.Equal(0, runtime.Engine.TotalAccepted);
            Assert.Equal(16, runtime.Engine.TotalRejected);
            Assert.Equal(0, runtime.Engine.State.Tick);
            Assert.Equal(GhostLapFailure.NoRunningMatch, registry.Submit(Item(18)));
        }
        finally
        {
            release.TrySetResult();
            await registry.StopAllAsync(ct);
        }
    }

    private sealed class TestAuthority(Guid generation) : IRealtimeAuthority
    {
        private volatile bool _valid = true;
        public Guid GenerationId { get; } = generation;
        public bool IsValid => _valid;
        public void Lose() => _valid = false;
    }

    private sealed class FinalTickClock : ManualClock
    {
        private int _parked;
        public MatchRuntime? Runtime { get; set; }
        public TaskCompletionSource FinalTickPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public override long GetTimestamp()
        {
            if (Runtime?.Engine.State.IsFinal == true && Interlocked.Exchange(ref _parked, 1) == 0)
            {
                FinalTickPublished.SetResult();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return base.GetTimestamp();
        }
    }

}
