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
        Events(f.Events), f.StateHash, f.IsFinal, f.DistanceFixed);

    /// <summary>The reducer's events of one tick in their stable order; the position in the array is the event's identity within the tick.</summary>
    public static GhostEventView[] Events(System.Collections.Immutable.ImmutableArray<GameEvent> events) =>
        [.. events.Select(e => new GhostEventView(e.Kind.ToString(), e.Slot, e.OtherSlot, e.Value, e.CandleId))];

    public static SoloFrame ToWire(WorldFrame w, bool final) =>
        new(w.Tick, w.XFixed, w.YFixed, w.Charge, w.OverlitDeadlineTick, w.TotalScore, final, w.DistanceFixed, w.IsOverlit, w.MovementPoints,
            w.PickupPoints, w.PenaltyPoints, w.Pickups, w.Bursts, w.BurstHits, w.HeldMoveX, w.HeldMoveY, w.BurstLatch, w.CollectedCount,
            w.LastSequence, [.. w.ActiveCandleIds], Events(w.Events));

    public static string KindName(SoloBranchKind kind) => kind == SoloBranchKind.Fork ? ChallengeKinds.Fork : ChallengeKinds.Challenge;

    public static SoloPinnedVersions Pinned(string rulesVersion, string layoutVersion) =>
        Pinned(GhostLap.Timeline.GhostLapTimelineVersions.V1, rulesVersion, layoutVersion);

    public static SoloPinnedVersions Pinned(Marrow.Timeline.TimelineVersionKey v, string rulesVersion, string layoutVersion)
    {
        return new SoloPinnedVersions(v.DomainId, v.ReducerVersion, v.InputSchemaVersion, v.StateSchemaVersion, v.ContextSchemaVersion,
            rulesVersion, layoutVersion);
    }

    public static ChallengeResultView ToView(SoloChallengeResult r) => new(
        r.Child.Value.ToString("D"), r.Original.Value.ToString("D"), KindName(r.Kind), r.OperationKey, r.ForkTick, r.FinalTick,
        r.ChildFinalStateHash, r.ChildLastRecordHash, r.LivingScore, r.SourceScore, r.Delta, r.SourceFinalStateHash, r.SourceLastRecordHash,
        r.Seed, r.MascotId, Pinned(r.RulesVersion, r.LayoutVersion));

    /// <summary>
    /// Classifies a refused input. A refused press of Burst whose rising edge has not been accepted yet is the one case where a one-shot
    /// action can be lost (<see cref="InputRejectionClasses.BurstRejected"/>); duplicate, late or early movement is benign.
    /// </summary>
    public static SoloInputRejected Describe(SoloInputItem? item, string code, bool burstEdgeAtRisk, long nextServerTick) => new(
        item?.Seq ?? -1, code, item?.RequestBurst == true,
        burstEdgeAtRisk ? InputRejectionClasses.BurstRejected
            : code is nameof(GhostLap.Simulation.InputRejection.StaleSequence) or nameof(GhostLap.Simulation.InputRejection.LateTick) or nameof(GhostLap.Simulation.InputRejection.FutureTick)
                or nameof(GhostLap.Simulation.InputRejection.CommitPending) ? InputRejectionClasses.BenignMovement
            : InputRejectionClasses.Refused,
        nextServerTick);

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

    /// <summary>Verified final hash of the original the prepared ghost came from (carried to the client at start).</summary>
    public string SourceFinalHash = "";

    // ---- telemetry (server scheduling vs delivery, input outcomes, coalesced one-shot events) ----
    private const int MaxMissedTicks = 64;
    private readonly Lock _telemetry = new();
    private readonly List<MissedTickEvents> _missed = [];
    private readonly List<double> _intervals = new(GameRulesV1.RoundTicks);
    private bool _missedTruncated;
    private int _ticks;
    private int _coalescedSinceLast;
    public long RunStartTimestamp;
    public long LastCommitTimestamp;
    public long Seq;
    public double MaxLagMs;
    public int CoalescedTotal;
    public int Admitted;
    public int BurstsAdmitted;
    public int RejectedBenign;
    public int RejectedBurst;
    public int RejectedOther;

    public void RecordCommit(double intervalMs, double lagMs)
    {
        lock (_telemetry)
        {
            _ticks++;
            if (intervalMs >= 0)
            {
                _intervals.Add(intervalMs);
            }

            MaxLagMs = Math.Max(MaxLagMs, lagMs);
        }
    }

    /// <summary>A tick whose frame was not pushed (the previous push was still in flight): keep its one-shot events for the next push.</summary>
    public void RecordSkipped(SoloChallengeTick tick)
    {
        lock (_telemetry)
        {
            _coalescedSinceLast++;
            CoalescedTotal++;
            if (tick.Living.Events.Length == 0 && tick.Ghost.Events.Length == 0)
            {
                return;
            }

            if (_missed.Count == MaxMissedTicks)
            {
                _missed.RemoveAt(0);
                _missedTruncated = true;
            }

            _missed.Add(new MissedTickEvents(tick.Tick, ChallengeMapping.Events(tick.Living.Events), ChallengeMapping.Events(tick.Ghost.Events)));
        }
    }

    public (MissedTickEvents[] Missed, bool Truncated, int Coalesced) DrainSkipped()
    {
        lock (_telemetry)
        {
            var result = (_missed.ToArray(), _missedTruncated, _coalescedSinceLast);
            _missed.Clear();
            _missedTruncated = false;
            _coalescedSinceLast = 0;
            return result;
        }
    }

    public SoloRunDiagnostics Diagnostics()
    {
        lock (_telemetry)
        {
            var sorted = _intervals.Order().ToArray();
            double Q(double q) => sorted.Length == 0 ? 0 : Math.Round(sorted[(int)((sorted.Length - 1) * q)], 3);
            return new SoloRunDiagnostics(
                _ticks, Q(.5), Q(.95), Q(.99), sorted.Length == 0 ? 0 : Math.Round(sorted[^1], 3),
                Math.Round(MaxLagMs, 3), CoalescedTotal, Admitted, BurstsAdmitted, RejectedBenign, RejectedBurst, RejectedOther);
        }
    }
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
    public Task<GhostLapReply<ChallengePrepared>> PrepareAsync(
        IActorContext actor, string connectionId, PrepareChallengeRequest request, CancellationToken ct) =>
        PrepareCoreAsync(actor, connectionId, request?.OriginalId, SoloBranchKind.Challenge, 0, ct);

    /// <summary>
    /// Task 11.5: the same lifecycle for a <see cref="SoloBranchKind.Fork"/> at an explicit tick 0..2699 (2700 is inspect-only). The tick is
    /// refused before any session, preparation slot or history read is taken.
    /// </summary>
    public Task<GhostLapReply<ChallengePrepared>> PrepareForkAsync(
        IActorContext actor, string connectionId, PrepareForkRequest request, CancellationToken ct) =>
        PrepareCoreAsync(actor, connectionId, request?.OriginalId, SoloBranchKind.Fork, request?.ForkTick ?? -1, ct);

    private async Task<GhostLapReply<ChallengePrepared>> PrepareCoreAsync(
        IActorContext actor, string connectionId, string? originalId, SoloBranchKind kind, long forkTick, CancellationToken ct)
    {
        if (originalId is null || !Guid.TryParseExact(originalId, "D", out var guid))
        {
            return GhostLapReply<ChallengePrepared>.Failure(SoloFailure.InvalidRequest);
        }

        if (kind == SoloBranchKind.Fork && forkTick == GameRulesV1.RoundTicks)
        {
            return GhostLapReply<ChallengePrepared>.Failure("fork_not_allowed");
        }

        if (kind == SoloBranchKind.Fork && (forkTick < 0 || forkTick > GameRulesV1.RoundTicks))
        {
            return GhostLapReply<ChallengePrepared>.Failure("invalid_tick");
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

                var created = new ChallengeSession(actor, kind == SoloBranchKind.Fork
                    ? challenges.BeginFork(actor, new TimelineId(guid), forkTick)
                    : challenges.Begin(actor, new TimelineId(guid)), connectionId);
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

            session.SourceFinalHash = trace.FinalStateHash;
            return GhostLapReply<ChallengePrepared>.Success(new ChallengePrepared(
                originalId, ChallengePhases.Ready, GameRulesV1.RoundTicks, trace.FinalStateHash, trace.RulesVersion,
                trace.LayoutVersion, GhostLapSoloWire.TickRateHz, ChallengeMapping.KindName(kind), forkTick, forkTick + 1,
                ChallengeMapping.Pinned(trace.RulesVersion, trace.LayoutVersion)));
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
            GhostLapSoloWire.TickRateHz, GameRulesV1.RoundTicks, ChallengeMapping.ToWire(run.Living, false), ChallengeMapping.ToWire(run.Ghost),
            ChallengeMapping.KindName(run.Kind), run.ForkTick, run.ForkTick + 1, session.SourceFinalHash,
            ChallengeMapping.Pinned(run.RulesVersion, run.LayoutVersion)));
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
        if (admitted.IsSuccess)
        {
            Interlocked.Increment(ref session.Admitted);
            if (item.RequestBurst)
            {
                Interlocked.Increment(ref session.BurstsAdmitted);
            }

            return null;
        }

        return admitted.Error.ToString();
    }

    /// <summary>
    /// The typed, classified notice for a refused input, and the one place refusals are counted. A refused Burst press whose rising edge the
    /// reducer has not seen yet is <see cref="InputRejectionClasses.BurstRejected"/> (the client must resend); duplicate or late movement is benign.
    /// </summary>
    public SoloInputRejected Describe(string subject, SoloInputItem? item, string code)
    {
        if (!_sessions.TryGetValue(subject, out var session))
        {
            return ChallengeMapping.Describe(item, code, item?.RequestBurst == true, 0);
        }

        var state = session.Challenge.LivingState;
        var edgeAtRisk = item?.RequestBurst == true && state is not null && !state.Players[0].BurstLatch;
        var notice = ChallengeMapping.Describe(item, code, edgeAtRisk, (state?.Tick ?? 0) + 1);
        switch (notice.Class)
        {
            case InputRejectionClasses.BenignMovement:
                Interlocked.Increment(ref session.RejectedBenign);
                break;
            case InputRejectionClasses.BurstRejected:
                Interlocked.Increment(ref session.RejectedBurst);
                break;
            default:
                Interlocked.Increment(ref session.RejectedOther);
                break;
        }

        return notice;
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
    public Task<ChallengeFinishView> FinishAsync(IActorContext actor, string clientKey, CancellationToken ct) =>
        FinishAsync(actor, clientKey, SoloBranchKind.Challenge, ct);

    /// <summary>
    /// <see cref="FinishAsync(IActorContext, string, CancellationToken)"/> for either kind: a Fork resolves through <c>ResolveForkAsync</c>,
    /// a Challenge through <c>ResolveAsync</c>. A live session only answers for its own kind and key.
    /// </summary>
    public async Task<ChallengeFinishView> FinishAsync(IActorContext actor, string clientKey, SoloBranchKind kind, CancellationToken ct)
    {
        if (_sessions.TryGetValue(actor.SubjectId, out var live) && live.ClientKey == clientKey && live.Challenge.Kind == kind)
        {
            if (Volatile.Read(ref live.Phase) != ChallengeRunPhase.Finishing)
            {
                return new ChallengeFinishView(ChallengeFinishStatuses.Failed, null, ChallengeFailure.NotComplete);
            }

            return ChallengeMapping.ToFinish(await CompleteSessionAsync(live, ct).ConfigureAwait(false));
        }

        return ChallengeMapping.ToFinish(kind == SoloBranchKind.Fork
            ? await challenges.ResolveForkAsync(actor, clientKey, ct).ConfigureAwait(false)
            : await challenges.ResolveAsync(actor, clientKey, ct).ConfigureAwait(false));
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
            var timer = new SoloTickClock();
            s.RunStartTimestamp = s.LastCommitTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
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
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(s.RunStartTimestamp, now).TotalMilliseconds;
                    var rounds = tick.Tick - s.Challenge.ForkTick;
                    var lagMs = elapsedMs - rounds * TickInterval.TotalMilliseconds;
                    s.RecordCommit(rounds > 1 ? System.Diagnostics.Stopwatch.GetElapsedTime(s.LastCommitTimestamp, now).TotalMilliseconds : -1, lagMs);
                    s.LastCommitTimestamp = now;
                    if (tick.IsFinal)
                    {
                        // The last frame carries the round's closing one-shot events, so it is delivered (bounded) rather than coalesced away.
                        await WaitForFreeSlotAsync(s).ConfigureAwait(false);
                        await PushFrame(s, tick, elapsedMs, lagMs).ConfigureAwait(false);
                        interrupted = false;
                        break;
                    }

                    _ = PushFrame(s, tick, elapsedMs, lagMs);
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
            await PushEndedAsync(s, new SoloRunEnded("interrupted", code, s.Diagnostics())).ConfigureAwait(false);
            return;
        }

        Volatile.Write(ref s.Phase, ChallengeRunPhase.Finishing);
        var diagnostics = s.Diagnostics();
        log.ChallengeTiming(s.RunId, diagnostics.Ticks, diagnostics.CommitIntervalP99Ms, diagnostics.CommitIntervalMaxMs, diagnostics.MaxScheduleLagMs,
            diagnostics.FramesCoalesced, diagnostics.RejectedBenignMovement, diagnostics.RejectedBurst);
        await PushEndedAsync(s, new SoloRunEnded("final", null, diagnostics)).ConfigureAwait(false);
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

    private static async Task WaitForFreeSlotAsync(ChallengeSession s)
    {
        for (var i = 0; i < 40 && Volatile.Read(ref s.FrameInFlight) != 0; i++)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pushes the committed tick unless the previous push is still in flight (latest wins, so a slow client never delays a tick). A skipped
    /// tick's one-shot events are kept and delivered with the next pushed frame, so no authoritative effect is lost.
    /// </summary>
    private Task PushFrame(ChallengeSession s, SoloChallengeTick tick, double commitElapsedMs, double lagMs)
    {
        if (Interlocked.CompareExchange(ref s.FrameInFlight, 1, 0) != 0)
        {
            s.RecordSkipped(tick);
            return Task.CompletedTask;
        }

        var (missed, truncated, coalesced) = s.DrainSkipped();
        var sendMs = System.Diagnostics.Stopwatch.GetElapsedTime(s.RunStartTimestamp).TotalMilliseconds;
        var frame = new ChallengeFrameView(
            tick.Tick, ChallengeMapping.ToWire(tick.Living, tick.IsFinal), ChallengeMapping.ToWire(tick.Ghost), tick.Delta, tick.IsFinal,
            ++s.Seq, new FramePacing(Math.Round(commitElapsedMs, 3), Math.Round(lagMs, 3), Math.Round(sendMs, 3), coalesced),
            missed.Length == 0 ? null : missed, truncated);
        return SendAsync(s, frame);
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
