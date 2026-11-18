using System.Collections.Concurrent;
using System.Security.Cryptography;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Solo;
using Marrow.Infrastructure.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.SignalR;

namespace GhostLapHost;

/// <summary>Maps the domain's solo save outcome and summaries onto the wire contracts. Nothing here invents a status.</summary>
public static class SoloMapping
{
    public static SoloOriginalView ToView(SoloOriginalSummary s) => new(
        s.Timeline.Value.ToString("D"), s.OperationKey, s.Seed, s.MascotId, s.RulesVersion, s.LayoutVersion,
        s.Versions.DomainId, s.Versions.ReducerVersion, s.Versions.InputSchemaVersion, s.Versions.StateSchemaVersion,
        s.Versions.ContextSchemaVersion, s.FinalTick, s.FinalStateHash, s.LastRecordHash, s.Score);

    public static SoloSaveView ToView(SoloSaveOutcome o) => o.Status switch
    {
        SoloSaveStatus.Saved when o.Original is { } original => new SoloSaveView(SoloSaveStatuses.Saved, ToView(original), null),
        SoloSaveStatus.SavePending => new SoloSaveView(SoloSaveStatuses.SavePending, null, Code(o.Error)),
        // A "Saved" without its verified original can never be reported as saved.
        _ => new SoloSaveView(SoloSaveStatuses.Failed, null, Code(o.Error) ?? "save_failed"),
    };

    public static string? Code(SoloError? error) => error?.Code switch
    {
        null => null,
        SoloErrorCode.NotFound => SoloFailure.NotFound,
        SoloErrorCode.Unauthenticated => SoloFailure.Forbidden,
        SoloErrorCode.InvalidRequest => SoloFailure.InvalidRequest,
        SoloErrorCode.InvalidTick => "invalid_tick",
        SoloErrorCode.IncompatibleVersion => "version_unsupported",
        SoloErrorCode.CorruptHistory => "corrupt_history",
        SoloErrorCode.Conflict => SoloFailure.Conflict,
        SoloErrorCode.Cancelled => SoloFailure.Unavailable,
        SoloErrorCode.Unavailable => SoloFailure.Unavailable,
        SoloErrorCode.NotComplete => "not_complete",
        SoloErrorCode.VerificationFailed => "verification_failed",
        SoloErrorCode.ForkNotAllowed => "fork_not_allowed",
        _ => "error",
    };
}

/// <summary>
/// The server's dedicated Ghost Lap solo database pool and engine owner (<c>ConnectionStrings:GhostLapSolo</c>, login
/// <c>sands_realtime_solo</c>: <c>marrow_timeline_runtime</c> and <c>ghostlap_runtime</c> only). Separate from the multiplayer login on purpose.
/// </summary>
public sealed class GhostLapSoloDataSource(Npgsql.NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public const string ApplicationName = "ghostlap-realtime-solo";

    public Npgsql.NpgsqlDataSource DataSource { get; } = dataSource;

    public static GhostLapSoloDataSource Create(string connectionString) =>
        new(Npgsql.NpgsqlDataSource.Create(new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = ApplicationName }.ConnectionString));

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}

/// <summary>Server-to-client contract of the solo hub.</summary>
public interface IGhostLapSoloClient
{
    Task Frame(SoloFrame frame);

    Task RunEnded(SoloRunEnded ended);

    Task InputRejected(SoloInputRejected rejected);

    /// <summary>The living state and the source ghost at the same tick (challenge, latest wins).</summary>
    Task ChallengeFrame(ChallengeFrameView frame);

    /// <summary>The challenge stopped: <c>final</c> (call FinishChallenge) or <c>interrupted</c> (no result).</summary>
    Task ChallengeEnded(SoloRunEnded ended);
}

internal static class SoloRunPhase
{
    /// <summary>Ticks are being committed.</summary>
    public const int Playing = 0;

    /// <summary>All 2700 ticks are committed; the verified save is in progress or pending a retry.</summary>
    public const int Saving = 1;
}

internal sealed class SoloRunSession(IActorContext actor, SoloRecording recording, string connectionId, SoloRunStarted started, CancellationToken parent)
{
    public IActorContext Actor { get; } = actor;

    public SoloRecording Recording { get; } = recording;

    public string ConnectionId { get; } = connectionId;

    public SoloRunStarted Started { get; } = started;

    public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(parent);

    /// <summary>One save attempt at a time: the recorder's completion must not overlap itself.</summary>
    public SemaphoreSlim SaveGate { get; } = new(1, 1);

    public int Phase;

    public int FrameInFlight;

    public long NextNoticeTimestamp;
}

/// <summary>
/// The Realtime process's solo recording sessions: at most one live run per guest and <see cref="MaxActiveRuns"/> in total.
/// Each run is a server-owned 30 Hz loop over <see cref="SoloRecording"/> (the Spec 06 recorder and the one Marrow Timeline). A run
/// whose connection drops, whose authority is lost or whose commits keep failing is <b>interrupted</b>: it stays an unlisted
/// Recording row and never becomes an original, and there is no resume of an unfinished run. Once all ticks are committed the run
/// is saved (verified) whether or not the client is still connected.
/// </summary>
public sealed class SoloRunRegistry(
    SoloOriginals originals,
    SoloOriginalStore store,
    IHubContext<GhostLapSoloHub, IGhostLapSoloClient> hub,
    RealtimeInstanceGuard guard,
    GhostLapSoloLog log,
    IHostApplicationLifetime lifetime)
{
    public const int MaxActiveRuns = 16;
    public const int MaxConcurrentHistoryReads = 4;
    public const int MaxConsecutiveCommitFailures = 30;
    public const int SaveAttempts = 3;
    public static readonly TimeSpan SaveRetryDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PendingSaveLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, SoloRunSession> _runs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _history = new(MaxConcurrentHistoryReads, MaxConcurrentHistoryReads);

    public SoloOriginals Originals { get; } = originals;

    public int ActiveRuns => _runs.Count;

    /// <summary>A slot for one replay-bearing read (library item, finish by key); null when the server is already doing as many as allowed.</summary>
    public IDisposable? TryEnterHistoryRead() => _history.Wait(0) ? new Lease(_history) : null;

    public async Task<GhostLapReply<SoloRunStarted>> StartAsync(IActorContext actor, string connectionId, StartSoloRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrEmpty(request.OperationKey) || request.OperationKey.Length > SoloOriginals.MaxOperationKeyLength
            || request.MascotId >= GameRulesV1.MascotCount)
        {
            return GhostLapReply<SoloRunStarted>.Failure(SoloFailure.InvalidRequest);
        }

        if (guard.AuthorityLost)
        {
            return GhostLapReply<SoloRunStarted>.Failure(SoloFailure.Unavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        try
        {
            await _startGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GhostLapReply<SoloRunStarted>.Failure(SoloFailure.Unavailable);
        }

        try
        {
            if (_runs.TryGetValue(actor.SubjectId, out var live))
            {
                if (live.Started.OperationKey == request.OperationKey && live.Started.MascotId != request.MascotId)
                {
                    return GhostLapReply<SoloRunStarted>.Failure(SoloFailure.Conflict);
                }

                // Repeating the same start on the same connection returns the same run; anything else is refused.
                return live.Started.OperationKey == request.OperationKey && live.ConnectionId == connectionId && Volatile.Read(ref live.Phase) == SoloRunPhase.Playing
                    ? GhostLapReply<SoloRunStarted>.Success(live.Started)
                    : GhostLapReply<SoloRunStarted>.Failure(SoloFailure.RunActive);
            }

            if (_runs.Count >= MaxActiveRuns)
            {
                return GhostLapReply<SoloRunStarted>.Failure(SoloFailure.ServerBusy);
            }

            var existing = await store.FindByOwnerKeyAsync(actor.SubjectId, request.OperationKey, timeout.Token).ConfigureAwait(false);
            if (existing.TryGetValue(out var row))
            {
                if (row.MascotId != request.MascotId)
                {
                    return GhostLapReply<SoloRunStarted>.Failure(SoloFailure.Conflict);
                }

                // Saved already, or an unfinished/interrupted run: never resumed, never replaced. The key stays what it was.
                return GhostLapReply<SoloRunStarted>.Failure(
                    row.Status == SoloOriginalStatus.Complete ? SoloFailure.AlreadySaved : SoloFailure.Interrupted);
            }

            if (existing.Error.Code != SoloErrorCode.NotFound)
            {
                return GhostLapReply<SoloRunStarted>.Failure(MappedStartCode(existing.Error));
            }

            var seed = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
            var created = await Originals.StartAsync(actor, seed, request.MascotId, request.OperationKey, timeout.Token).ConfigureAwait(false);
            if (!created.TryGetValue(out var recording))
            {
                return GhostLapReply<SoloRunStarted>.Failure(MappedStartCode(created.Error));
            }

            if (recording.State.Tick != 0)
            {
                // The key was created concurrently elsewhere and already has committed ticks: never continue someone else's prefix.
                return GhostLapReply<SoloRunStarted>.Failure(SoloFailure.Interrupted);
            }

            var started = new SoloRunStarted(
                request.OperationKey, recording.Timeline.Value.ToString("D"), recording.Context.Seed, request.MascotId,
                recording.Context.RulesVersion, recording.Context.LayoutVersion, GhostLapSoloWire.TickRateHz, GameRulesV1.RoundTicks);
            var session = new SoloRunSession(actor, recording, connectionId, started, lifetime.ApplicationStopping);
            if (!_runs.TryAdd(actor.SubjectId, session))
            {
                session.Cts.Dispose();
                return GhostLapReply<SoloRunStarted>.Failure(SoloFailure.RunActive);
            }

            log.RunStarted(started.RunId);
            _ = Task.Run(() => RunAsync(session), CancellationToken.None);
            return GhostLapReply<SoloRunStarted>.Success(started);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Admits one client input for the guest's live run on this connection. Null when accepted, otherwise the refusal code.</summary>
    public string? Admit(string subject, string connectionId, SoloInputItem item)
    {
        if (item is null || !_runs.TryGetValue(subject, out var session) || session.ConnectionId != connectionId
            || Volatile.Read(ref session.Phase) != SoloRunPhase.Playing)
        {
            return SoloFailure.NoRun;
        }

        var admitted = session.Recording.Submit(new SubmittedInput(0, item.Seq, item.ClientTick, item.MoveX, item.MoveY, item.RequestBurst));
        return admitted.IsSuccess ? null : admitted.Error.ToString();
    }

    /// <summary>Whether this connection currently drives a live run (so a notice or a second stream can be refused cheaply).</summary>
    public bool Drives(string subject, string connectionId) =>
        _runs.TryGetValue(subject, out var s) && s.ConnectionId == connectionId && Volatile.Read(ref s.Phase) == SoloRunPhase.Playing;

    /// <summary>A closed connection interrupts its run while it is still being played. A finished run keeps saving.</summary>
    public void ConnectionClosed(string subject, string connectionId)
    {
        if (_runs.TryGetValue(subject, out var s) && s.ConnectionId == connectionId && Volatile.Read(ref s.Phase) == SoloRunPhase.Playing)
        {
            Cancel(s);
        }
    }

    /// <summary>
    /// The honest save state for <paramref name="operationKey"/>: retries the in-process save of a finished run, otherwise resolves
    /// the owner's key from the database (identical identity on repeat). A finished run whose save was never confirmed (for example
    /// after a restart) is completed from its committed history; an unfinished run is NotComplete.
    /// </summary>
    public async Task<SoloSaveOutcome> FinishAsync(IActorContext actor, string operationKey, CancellationToken ct)
    {
        if (_runs.TryGetValue(actor.SubjectId, out var live) && live.Started.OperationKey == operationKey)
        {
            if (Volatile.Read(ref live.Phase) == SoloRunPhase.Playing)
            {
                return SoloSaveOutcome.Failed(new SoloError(SoloErrorCode.NotComplete, "The run is still being played."));
            }

            return await SaveSessionAsync(live, ct).ConfigureAwait(false);
        }

        var resolved = await Originals.ResolveAsync(actor, operationKey, ct).ConfigureAwait(false);
        if (resolved.Error?.Code != SoloErrorCode.NotComplete)
        {
            return resolved;
        }

        // The row exists but is not Complete: either never finished (interrupted) or finished with its save unconfirmed.
        var row = await store.FindByOwnerKeyAsync(actor.SubjectId, operationKey, ct).ConfigureAwait(false);
        if (!row.TryGetValue(out var recorded) || recorded.Status != SoloOriginalStatus.Recording)
        {
            return resolved;
        }

        var resumed = await Originals.StartAsync(actor, recorded.Seed, recorded.MascotId, operationKey, ct).ConfigureAwait(false);
        if (!resumed.TryGetValue(out var recording))
        {
            return resumed.Error.Code is SoloErrorCode.Unavailable or SoloErrorCode.Cancelled
                ? SoloSaveOutcome.Pending(resumed.Error)
                : SoloSaveOutcome.Failed(resumed.Error);
        }

        return recording.IsRoundOver
            ? await Originals.SaveAsync(actor, recording, ct).ConfigureAwait(false)
            : resolved;
    }

    private async Task<SoloSaveOutcome> SaveSessionAsync(SoloRunSession session, CancellationToken ct)
    {
        await session.SaveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var outcome = await Originals.SaveAsync(session.Actor, session.Recording, ct).ConfigureAwait(false);
            log.SaveOutcome(session.Started.RunId, outcome.Status.ToString(), SoloMapping.Code(outcome.Error));
            if (outcome.Status != SoloSaveStatus.SavePending)
            {
                Remove(session);
            }

            return outcome;
        }
        finally
        {
            session.SaveGate.Release();
        }
    }

    private async Task RunAsync(SoloRunSession s)
    {
        try
        {
            await PlayAsync(s).ConfigureAwait(false);
        }
        finally
        {
            Remove(s);
            s.Cts.Dispose();
        }
    }

    private async Task PlayAsync(SoloRunSession s)
    {
        var token = s.Cts.Token;
        var interrupted = true;
        string? code = null;
        try
        {
            var timer = new SoloTickClock();
            var failures = 0;
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (guard.AuthorityLost)
                {
                    code = SoloFailure.Unavailable;
                    break;
                }

                var committed = await s.Recording.CommitTickAsync(token).ConfigureAwait(false);
                if (committed.TryGetValue(out var receipt))
                {
                    failures = 0;
                    PushFrame(s, receipt.State);
                    if (receipt.State.IsFinal)
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

                code = SoloMapping.Code(committed.Error);
                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.RunFailed(e, s.Started.RunId);
            code = "error";
        }

        if (interrupted)
        {
            log.RunInterrupted(s.Started.RunId, s.Recording.State.Tick, code ?? "disconnected");
            Remove(s);
            await PushEndedAsync(s, new SoloRunEnded("interrupted", code)).ConfigureAwait(false);
            return;
        }

        Volatile.Write(ref s.Phase, SoloRunPhase.Saving);
        await PushEndedAsync(s, new SoloRunEnded("final", null)).ConfigureAwait(false);
        await SaveAfterFinalAsync(s).ConfigureAwait(false);
    }

    /// <summary>Saves a finished run a few times; a still-unconfirmed save stays retryable through FinishAsync for a bounded time.</summary>
    private async Task SaveAfterFinalAsync(SoloRunSession s)
    {
        var stop = lifetime.ApplicationStopping;
        try
        {
            for (var attempt = 0; attempt < SaveAttempts; attempt++)
            {
                var outcome = await SaveSessionAsync(s, stop).ConfigureAwait(false);
                if (outcome.Status != SoloSaveStatus.SavePending)
                {
                    return;
                }

                await Task.Delay(SaveRetryDelay, stop).ConfigureAwait(false);
            }

            // Still unconfirmed: keep the recorder retryable through FinishAsync for a bounded time (Remove cancels this wait).
            await Task.Delay(PendingSaveLifetime, s.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            log.RunFailed(e, s.Started.RunId);
        }
        finally
        {
            Remove(s);
        }
    }

    private void PushFrame(SoloRunSession s, GameState state)
    {
        if (Interlocked.CompareExchange(ref s.FrameInFlight, 1, 0) != 0)
        {
            return; // latest wins: a client that is not reading never delays a tick
        }

        var p = state.Players[0];
        var frame = new SoloFrame(state.Tick, p.XFixed, p.YFixed, p.Charge, p.OverlitDeadlineTick, p.TotalScore, state.IsFinal);
        _ = SendAsync(s, frame);
    }

    private async Task SendAsync(SoloRunSession s, SoloFrame frame)
    {
        try
        {
            await hub.Clients.Client(s.ConnectionId).Frame(frame).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
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

    private async Task PushEndedAsync(SoloRunSession s, SoloRunEnded ended)
    {
        try
        {
            await hub.Clients.Client(s.ConnectionId).RunEnded(ended).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>True at most about every 50 ms per run, so rejection notices cannot flood a client.</summary>
    public bool TryTakeNotice(string subject)
    {
        if (!_runs.TryGetValue(subject, out var s))
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

    private void Remove(SoloRunSession s)
    {
        if (_runs.TryRemove(new KeyValuePair<string, SoloRunSession>(s.Actor.SubjectId, s)))
        {
            Cancel(s);
        }
    }

    private static void Cancel(SoloRunSession s)
    {
        try
        {
            s.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string MappedStartCode(SoloError error) => error.Code switch
    {
        SoloErrorCode.Conflict => SoloFailure.Conflict,
        SoloErrorCode.InvalidRequest => SoloFailure.InvalidRequest,
        SoloErrorCode.Unauthenticated => SoloFailure.Forbidden,
        _ => SoloFailure.Unavailable,
    };

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
