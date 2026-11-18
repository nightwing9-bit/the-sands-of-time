using System.Collections.Concurrent;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using Marrow.Infrastructure.Realtime;
using Marrow.Kernel.Security;
using Marrow.Timeline;
using Microsoft.AspNetCore.SignalR;

namespace GhostLapHost;

/// <summary>Maps challenge domain values onto the wire contracts. Nothing here invents a status or a history value.</summary>
public static class ChallengeMapping
{
    public static SoloGhostFrame ToWire(SoloTraceFrame f) => new(
        f.Tick, f.XFixed, f.YFixed, f.Charge, f.IsOverlit, f.OverlitDeadlineTick, f.MovementPoints, f.PickupPoints, f.PenaltyPoints,
        f.TotalScore, f.Pickups, f.Bursts, f.HeldMoveX, f.HeldMoveY, f.BurstLatch, f.CollectedCount, [.. f.ActiveCandleIds],
        [.. f.Events.Select(e => new GhostEventView(e.Kind.ToString(), e.Slot, e.OtherSlot, e.Value, e.CandleId))], f.StateHash, f.IsFinal);

    public static SoloFrame ToWire(WorldFrame w, bool final) =>
        new(w.Tick, w.XFixed, w.YFixed, w.Charge, w.OverlitDeadlineTick, w.TotalScore, final);

    public static ChallengeResultView ToView(SoloChallengeResult r) => new(
        r.Child.Value.ToString("D"), r.Original.Value.ToString("D"), r.Kind.ToString(), r.OperationKey, r.ForkTick, r.FinalTick,
        r.ChildFinalStateHash, r.ChildLastRecordHash, r.LivingScore, r.SourceScore, r.Delta, r.SourceFinalStateHash, r.SourceLastRecordHash);

    /// <summary>Completed only with a verified result; a transient failure is Pending (same key retries), anything else Failed.</summary>
    public static ChallengeFinishView ToFinish(Marrow.Kernel.Results.Result<SoloChallengeResult, SoloError> outcome)
    {
        if (outcome.TryGetValue(out var result))
        {
            return new ChallengeFinishView(ChallengeFinishStatuses.Completed, ToView(result), null);
        }

        var error = outcome.Error;
        return error.Code is SoloErrorCode.Unavailable or SoloErrorCode.Cancelled
            ? new ChallengeFinishView(ChallengeFinishStatuses.Pending, null, SoloMapping.Code(error))
            : new ChallengeFinishView(ChallengeFinishStatuses.Failed, null,
                error.Code == SoloErrorCode.NotComplete ? ChallengeFailure.NotComplete : SoloMapping.Code(error));
    }

    public static string Code(SoloError error) => error.Code == SoloErrorCode.NotComplete ? ChallengeFailure.NotComplete : SoloMapping.Code(error) ?? "error";
}

internal static class ChallengeRunPhase
{
    /// <summary>Preparing or Ready: no tick has been committed.</summary>
    public const int Setup = 0;

    public const int Playing = 1;

    /// <summary>All 2700 ticks are committed; the child is being completed and verified.</summary>
    public const int Finishing = 2;
}

internal sealed class ChallengeSession(IActorContext actor, SoloChallenge challenge, string connectionId)
{
    public IActorContext Actor { get; } = actor;

    public SoloChallenge Challenge { get; } = challenge;

    public string ConnectionId { get; } = connectionId;

    /// <summary>Cancelled when the session is removed. Deliberately not linked to the application lifetime, so an abandoned session holds no registration.</summary>
    public CancellationTokenSource Cts { get; } = new();

    public SemaphoreSlim FinishGate { get; } = new(1, 1);

    public string? ClientKey;

    public string RunId = "";

    public int Phase;

    public int FrameInFlight;

    public long NextNoticeTimestamp;
}

/// <summary>
/// The Realtime process's challenge sessions: one per guest, <see cref="MaxActive"/> in total, at most
/// <see cref="MaxConcurrentPreparations"/> trace preparations at once. Each running challenge is a server-owned 30 Hz loop over
/// <see cref="SoloChallenge"/>; the ghost of every tick is a local lookup in the prepared trace, so a running challenge makes no
/// history read. A challenge whose connection drops, whose authority is lost or whose commits keep failing is interrupted and
/// never becomes a result; there is no resume.
/// </summary>
public sealed class SoloChallengeRegistry(
    SoloChallenges challenges,
    IHubContext<GhostLapSoloHub, IGhostLapSoloClient> hub,
    RealtimeInstanceGuard guard,
    GhostLapSoloLog log,
    IHostApplicationLifetime lifetime)
{
    public const int MaxActive = 8;
    public const int MaxConcurrentPreparations = 2;
    public const int MaxConsecutiveCommitFailures = 30;
    public const int CompleteAttempts = 3;
    public static readonly TimeSpan CompleteRetryDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan TickInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / GhostLapSoloWire.TickRateHz);

    private readonly ConcurrentDictionary<string, ChallengeSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _registerGate = new(1, 1);
    private readonly SemaphoreSlim _preparations = new(MaxConcurrentPreparations, MaxConcurrentPreparations);

    public int Active => _sessions.Count;

    public GhostLap.Solo.SoloGhostTraceCache Cache => challenges.Cache;

    /// <summary>Authenticates and verifies the guest's original and holds its full ghost trace. Returns only after the trace is Ready or the failure is known.</summary>
    public async Task<GhostLapReply<ChallengePrepared>> PrepareAsync(
        IActorContext actor, string connectionId, PrepareChallengeRequest request, CancellationToken ct)
    {
        if (request is null || !Guid.TryParseExact(request.OriginalId, "D", out var guid))
        {
            return GhostLapReply<ChallengePrepared>.Failure(SoloFailure.InvalidRequest);
        }

        if (guard.AuthorityLost)
        {
            return GhostLapReply<ChallengePrepared>.Failure(SoloFailure.Unavailable);
        }

        if (!_preparations.Wait(0))
        {
            return GhostLapReply<ChallengePrepared>.Failure(ChallengeFailure.PreparationBusy);
        }

        ChallengeSession? session = null;
        try
        {
            await _registerGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_sessions.TryGetValue(actor.SubjectId, out var existing))
                {
                    if (Volatile.Read(ref existing.Phase) != ChallengeRunPhase.Setup)
                    {
                        return GhostLapReply<ChallengePrepared>.Failure(ChallengeFailure.ChallengeActive);
                    }

                    // An unstarted challenge is replaced by the new selection.
                    Remove(existing);
                }

                if (_sessions.Count >= MaxActive)
                {
                    return GhostLapReply<ChallengePrepared>.Failure(SoloFailure.ServerBusy);
                }

                var created = new ChallengeSession(actor, challenges.Begin(actor, new TimelineId(guid)), connectionId);
                if (!_sessions.TryAdd(actor.SubjectId, created))
                {
                    created.Cts.Dispose();
                    return GhostLapReply<ChallengePrepared>.Failure(ChallengeFailure.ChallengeActive);
                }

                session = created;
            }
            finally
            {
                _registerGate.Release();
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token, lifetime.ApplicationStopping);
            var prepared = await session.Challenge.PrepareAsync(linked.Token).ConfigureAwait(false);
            if (!prepared.TryGetValue(out var trace))
            {
                log.ChallengePrepareFailed(prepared.Error.Code.ToString());
                Remove(session);
                return GhostLapReply<ChallengePrepared>.Failure(ChallengeMapping.Code(prepared.Error));
            }

            return GhostLapReply<ChallengePrepared>.Success(new ChallengePrepared(
                request.OriginalId, ChallengePhases.Ready, GameRulesV1.RoundTicks, trace.FinalStateHash, trace.RulesVersion,
                trace.LayoutVersion, GhostLapSoloWire.TickRateHz));
        }
        catch (OperationCanceledException)
        {
            if (session is not null)
            {
                Remove(session);
            }

            return GhostLapReply<ChallengePrepared>.Failure(SoloFailure.Unavailable);
        }
        finally
        {
            _preparations.Release();
        }
    }

    /// <summary>Starts the living challenge for the guest's Ready challenge on this connection. Never with an unprepared or failed trace.</summary>
    public async Task<GhostLapReply<ChallengeStarted>> StartAsync(
        IActorContext actor, string connectionId, StartChallengeRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrEmpty(request.OperationKey))
        {
            return GhostLapReply<ChallengeStarted>.Failure(SoloFailure.InvalidRequest);
        }

        if (guard.AuthorityLost)
        {
            return GhostLapReply<ChallengeStarted>.Failure(SoloFailure.Unavailable);
        }

        if (!_sessions.TryGetValue(actor.SubjectId, out var session) || session.ConnectionId != connectionId
            || Volatile.Read(ref session.Phase) != ChallengeRunPhase.Setup || session.Challenge.Phase != SoloChallengePhase.Ready)
        {
            return GhostLapReply<ChallengeStarted>.Failure(ChallengeFailure.NotReady);
        }

        var started = await session.Challenge.StartAsync(request.OperationKey, ct).ConfigureAwait(false);
        if (!started.TryGetValue(out var run))
        {
            if (session.Challenge.Phase is not SoloChallengePhase.Ready)
            {
                Remove(session);
            }

            return GhostLapReply<ChallengeStarted>.Failure(ChallengeMapping.Code(started.Error));
        }

        session.ClientKey = request.OperationKey;
        session.RunId = run.Child.Value.ToString("D");
        if (Interlocked.CompareExchange(ref session.Phase, ChallengeRunPhase.Playing, ChallengeRunPhase.Setup) != ChallengeRunPhase.Setup)
        {
            return GhostLapReply<ChallengeStarted>.Failure(ChallengeFailure.NotReady);
        }

        log.ChallengeStarted(session.RunId);
        _ = Task.Run(() => RunAsync(session), CancellationToken.None);
        return GhostLapReply<ChallengeStarted>.Success(new ChallengeStarted(
            run.OperationKey, session.RunId, run.Original.Value.ToString("D"), run.Seed, run.MascotId, run.RulesVersion, run.LayoutVersion,
            GhostLapSoloWire.TickRateHz, GameRulesV1.RoundTicks, ChallengeMapping.ToWire(run.Living, false), ChallengeMapping.ToWire(run.Ghost)));
    }

    /// <summary>Admits one client input for the guest's running challenge on this connection. Null when accepted, otherwise the refusal code.</summary>
    public string? Admit(string subject, string connectionId, SoloInputItem item)
    {
        if (item is null || !_sessions.TryGetValue(subject, out var session) || session.ConnectionId != connectionId
            || Volatile.Read(ref session.Phase) != ChallengeRunPhase.Playing)
        {
            return SoloFailure.NoRun;
        }

        var admitted = session.Challenge.Submit(new SubmittedInput(0, item.Seq, item.ClientTick, item.MoveX, item.MoveY, item.RequestBurst));
        return admitted.IsSuccess ? null : admitted.Error.ToString();
    }

    public bool Drives(string subject, string connectionId) =>
        _sessions.TryGetValue(subject, out var s) && s.ConnectionId == connectionId && Volatile.Read(ref s.Phase) == ChallengeRunPhase.Playing;

    /// <summary>A closed connection discards a challenge that is still being prepared or played. A finishing one keeps completing.</summary>
    public void ConnectionClosed(string subject, string connectionId)
    {
        if (_sessions.TryGetValue(subject, out var s) && s.ConnectionId == connectionId && Volatile.Read(ref s.Phase) != ChallengeRunPhase.Finishing)
        {
            Remove(s);
        }
    }

    /// <summary>Discards the guest's own unfinished challenge. True when one was discarded.</summary>
    public bool Cancel(string subject)
    {
        if (_sessions.TryGetValue(subject, out var s) && Volatile.Read(ref s.Phase) != ChallengeRunPhase.Finishing)
        {
            Remove(s);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The honest result state for <paramref name="clientKey"/>: completes (idempotently) a finished in-process challenge, otherwise
    /// resolves the owner's stored child by key. A challenge still being played is NotComplete.
    /// </summary>
    public async Task<ChallengeFinishView> FinishAsync(IActorContext actor, string clientKey, CancellationToken ct)
    {
        if (_sessions.TryGetValue(actor.SubjectId, out var live) && live.ClientKey == clientKey)
        {
            if (Volatile.Read(ref live.Phase) != ChallengeRunPhase.Finishing)
            {
                return new ChallengeFinishView(ChallengeFinishStatuses.Failed, null, ChallengeFailure.NotComplete);
            }

            return ChallengeMapping.ToFinish(await CompleteSessionAsync(live, ct).ConfigureAwait(false));
        }

        return ChallengeMapping.ToFinish(await challenges.ResolveAsync(actor, clientKey, ct).ConfigureAwait(false));
    }

    private async Task<Marrow.Kernel.Results.Result<SoloChallengeResult, SoloError>> CompleteSessionAsync(ChallengeSession s, CancellationToken ct)
    {
        await s.FinishGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var outcome = await s.Challenge.CompleteAsync(ct).ConfigureAwait(false);
            log.ChallengeEnded(s.RunId, outcome.IsSuccess ? "Completed" : outcome.Error.Code.ToString());
            if (outcome.IsSuccess || outcome.Error.Code is not (SoloErrorCode.Unavailable or SoloErrorCode.Cancelled))
            {
                Remove(s);
            }

            return outcome;
        }
        finally
        {
            s.FinishGate.Release();
        }
    }

    private async Task RunAsync(ChallengeSession s)
    {
        try
        {
            await PlayAsync(s).ConfigureAwait(false);
        }
        finally
        {
            Remove(s);
        }
    }

    private async Task PlayAsync(ChallengeSession s)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token, lifetime.ApplicationStopping);
        var token = linked.Token;
        var interrupted = true;
        string? code = null;
        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            var failures = 0;
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (guard.AuthorityLost)
                {
                    code = SoloFailure.Unavailable;
                    break;
                }

                var committed = await s.Challenge.CommitTickAsync(token).ConfigureAwait(false);
                if (committed.TryGetValue(out var tick))
                {
                    failures = 0;
                    PushFrame(s, tick);
                    if (tick.IsFinal)
                    {
                        interrupted = false;
                        break;
                    }

                    continue;
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                // A failed commit froze this tick's step: the next attempt sends the identical payload.
                if (committed.Error.Code == SoloErrorCode.Unavailable && ++failures < MaxConsecutiveCommitFailures)
                {
                    continue;
                }

                code = ChallengeMapping.Code(committed.Error);
                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.RunFailed(e, s.RunId);
            code = "error";
        }

        if (interrupted)
        {
            log.RunInterrupted(s.RunId, s.Challenge.LivingState?.Tick ?? 0, code ?? "disconnected");
            Remove(s);
            await PushEndedAsync(s, new SoloRunEnded("interrupted", code)).ConfigureAwait(false);
            return;
        }

        Volatile.Write(ref s.Phase, ChallengeRunPhase.Finishing);
        await PushEndedAsync(s, new SoloRunEnded("final", null)).ConfigureAwait(false);
        await CompleteAfterFinalAsync(s).ConfigureAwait(false);
    }

    /// <summary>Completes the finished child a few times; a still-unconfirmed result stays retryable through FinishAsync for a bounded time.</summary>
    private async Task CompleteAfterFinalAsync(ChallengeSession s)
    {
        var stop = lifetime.ApplicationStopping;
        try
        {
            for (var attempt = 0; attempt < CompleteAttempts; attempt++)
            {
                var outcome = await CompleteSessionAsync(s, stop).ConfigureAwait(false);
                if (outcome.IsSuccess || outcome.Error.Code is not (SoloErrorCode.Unavailable or SoloErrorCode.Cancelled))
                {
                    return;
                }

                await Task.Delay(CompleteRetryDelay, stop).ConfigureAwait(false);
            }

            await Task.Delay(PendingLifetime, s.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.RunFailed(e, s.RunId);
        }
    }

    private void PushFrame(ChallengeSession s, SoloChallengeTick tick)
    {
        if (Interlocked.CompareExchange(ref s.FrameInFlight, 1, 0) != 0)
        {
            return; // latest wins: a client that is not reading never delays a tick
        }

        var frame = new ChallengeFrameView(
            tick.Tick, ChallengeMapping.ToWire(tick.Living, tick.IsFinal), ChallengeMapping.ToWire(tick.Ghost), tick.Delta, tick.IsFinal);
        _ = SendAsync(s, frame);
    }

    private async Task SendAsync(ChallengeSession s, ChallengeFrameView frame)
    {
        try
        {
            await hub.Clients.Client(s.ConnectionId).ChallengeFrame(frame).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Delivery is best effort: the committed history, not the push, is the truth.
        }
        finally
        {
            Volatile.Write(ref s.FrameInFlight, 0);
        }
    }

    private async Task PushEndedAsync(ChallengeSession s, SoloRunEnded ended)
    {
        try
        {
            await hub.Clients.Client(s.ConnectionId).ChallengeEnded(ended).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>True at most about every 50 ms per challenge, so rejection notices cannot flood a client.</summary>
    public bool TryTakeNotice(string subject)
    {
        if (!_sessions.TryGetValue(subject, out var s))
        {
            return false;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var next = Interlocked.Read(ref s.NextNoticeTimestamp);
        if (now < next)
        {
            return false;
        }

        Interlocked.Exchange(ref s.NextNoticeTimestamp, now + System.Diagnostics.Stopwatch.Frequency / 20);
        return true;
    }

    /// <summary>Drops the session, cancels its work and releases its in-memory ghost trace.</summary>
    private void Remove(ChallengeSession s)
    {
        if (_sessions.TryRemove(new KeyValuePair<string, ChallengeSession>(s.Actor.SubjectId, s)))
        {
            s.Challenge.Cancel();
            try
            {
                s.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
