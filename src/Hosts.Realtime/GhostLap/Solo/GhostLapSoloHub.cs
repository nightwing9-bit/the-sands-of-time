using System.Diagnostics;
using GhostLap.Contracts.Multiplayer;
using GhostLap.Contracts.Solo;
using Marrow.Infrastructure.Identity;
using Marrow.Infrastructure.Realtime;
using Marrow.Kernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GhostLapHost;

/// <summary>
/// Thin ingress to the <see cref="SoloRunRegistry"/>: a guest starts one 30 Hz solo run, streams inputs and asks for its save status.
/// The caller's identity is the verified guest session of the connection, never a request field, and the hub carries no game state.
/// Inputs travel on ONE client-to-server stream (the shared rate filter counts invocations, not items); the item rate is bounded
/// here and a connection that keeps flooding is closed.
/// </summary>
[Authorize(Policy = GhostLapPolicies.Connect)]
public sealed class GhostLapSoloHub(
    SoloRunRegistry runs, SoloChallengeRegistry challenges, GhostLapSessionAccess access, RealtimeInstanceGuard guard, GhostLapSoloLog log)
    : Hub<IGhostLapSoloClient>
{
    /// <summary>Items per second one stream may send (a 30 Hz client with headroom) and its burst.</summary>
    public const int InputsPerSecond = 60;
    public const int InputBurst = 60;

    private const string StreamOpenKey = "solo.stream-open";

    public override Task OnConnectedAsync()
    {
        if (!GhostLapSession.TryRead(Context.User, out _))
        {
            Context.Abort();
        }

        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (GhostLapSession.TryRead(Context.User, out var session))
        {
            runs.ConnectionClosed(session.Subject, Context.ConnectionId);
            challenges.ConnectionClosed(session.Subject, Context.ConnectionId);
        }

        return Task.CompletedTask;
    }

    public async Task<GhostLapReply<SoloRunStarted>> StartRun(StartSoloRequest request)
    {
        var gate = await AuthorizeAsync();
        return gate.Failure is not null
            ? GhostLapReply<SoloRunStarted>.Failure(gate.Failure)
            : await runs.StartAsync(gate.Actor!, Context.ConnectionId, request, Context.ConnectionAborted);
    }

    /// <summary>
    /// Idempotent save status for the run's operation key: <c>Saved</c> (with the verified identity, hash and versions), <c>SavePending</c>
    /// (retry the same key) or <c>Failed</c>. Safe after a lost response, a refresh or a host restart.
    /// </summary>
    public async Task<GhostLapReply<SoloSaveView>> FinishRun(string operationKey)
    {
        var gate = await AuthorizeAsync();
        if (gate.Failure is not null)
        {
            return GhostLapReply<SoloSaveView>.Failure(gate.Failure);
        }

        if (string.IsNullOrEmpty(operationKey) || operationKey.Length > GhostLap.Solo.SoloOriginals.MaxOperationKeyLength)
        {
            return GhostLapReply<SoloSaveView>.Failure(SoloFailure.InvalidRequest);
        }

        using var slot = runs.TryEnterHistoryRead();
        if (slot is null)
        {
            return GhostLapReply<SoloSaveView>.Failure(SoloFailure.ServerBusy);
        }

        var outcome = await runs.FinishAsync(gate.Actor!, operationKey, Context.ConnectionAborted);
        return GhostLapReply<SoloSaveView>.Success(SoloMapping.ToView(outcome));
    }

    /// <summary>Gameplay ingress for the guest's own live run. One stream per connection; the reply summarizes it when it completes.</summary>
    public async Task<GhostLapReply<InputStreamSummary>> StreamInputs(IAsyncEnumerable<SoloInputItem> inputs)
    {
        var gate = await AuthorizeAsync();
        if (gate.Failure is not null)
        {
            return GhostLapReply<InputStreamSummary>.Failure(gate.Failure);
        }

        var subject = gate.Actor!.SubjectId;
        if (!runs.Drives(subject, Context.ConnectionId))
        {
            return GhostLapReply<InputStreamSummary>.Failure(SoloFailure.NoRun);
        }

        return await ReadInputsAsync(inputs, subject, item => runs.Admit(subject, Context.ConnectionId, item), () => runs.TryTakeNotice(subject));
    }

    /// <summary>
    /// Authenticates the guest's saved original and holds its full verified ghost trace. The reply is Ready or a typed failure; a
    /// challenge can only start after it, and never with a partial trace.
    /// </summary>
    public async Task<GhostLapReply<ChallengePrepared>> PrepareChallenge(PrepareChallengeRequest request)
    {
        var gate = await AuthorizeAsync();
        return gate.Failure is not null
            ? GhostLapReply<ChallengePrepared>.Failure(gate.Failure)
            : await challenges.PrepareAsync(gate.Actor!, Context.ConnectionId, request, Context.ConnectionAborted);
    }

    /// <summary>Starts the living challenge from the original's exact tick zero. Only from Ready on this connection.</summary>
    public async Task<GhostLapReply<ChallengeStarted>> StartChallenge(StartChallengeRequest request)
    {
        var gate = await AuthorizeAsync();
        return gate.Failure is not null
            ? GhostLapReply<ChallengeStarted>.Failure(gate.Failure)
            : await challenges.StartAsync(gate.Actor!, Context.ConnectionId, request, Context.ConnectionAborted);
    }

    /// <summary>Discards the guest's own unfinished challenge (never a result). The saved original and earlier children are untouched.</summary>
    public async Task<GhostLapReply<bool>> CancelChallenge()
    {
        var gate = await AuthorizeAsync();
        return gate.Failure is not null
            ? GhostLapReply<bool>.Failure(gate.Failure)
            : GhostLapReply<bool>.Success(challenges.Cancel(gate.Actor!.SubjectId));
    }

    /// <summary>Idempotent challenge result for the key: <c>Completed</c> with the verified child, <c>Pending</c> (retry) or <c>Failed</c>.</summary>
    public async Task<GhostLapReply<ChallengeFinishView>> FinishChallenge(string operationKey)
    {
        var gate = await AuthorizeAsync();
        if (gate.Failure is not null)
        {
            return GhostLapReply<ChallengeFinishView>.Failure(gate.Failure);
        }

        if (string.IsNullOrEmpty(operationKey) || operationKey.Length > GhostLap.Solo.SoloOriginals.MaxOperationKeyLength)
        {
            return GhostLapReply<ChallengeFinishView>.Failure(SoloFailure.InvalidRequest);
        }

        using var slot = runs.TryEnterHistoryRead();
        if (slot is null)
        {
            return GhostLapReply<ChallengeFinishView>.Failure(SoloFailure.ServerBusy);
        }

        return GhostLapReply<ChallengeFinishView>.Success(await challenges.FinishAsync(gate.Actor!, operationKey, Context.ConnectionAborted));
    }

    /// <summary>Gameplay ingress for the guest's own running challenge. One stream per connection.</summary>
    public async Task<GhostLapReply<InputStreamSummary>> StreamChallengeInputs(IAsyncEnumerable<SoloInputItem> inputs)
    {
        var gate = await AuthorizeAsync();
        if (gate.Failure is not null)
        {
            return GhostLapReply<InputStreamSummary>.Failure(gate.Failure);
        }

        var subject = gate.Actor!.SubjectId;
        if (!challenges.Drives(subject, Context.ConnectionId))
        {
            return GhostLapReply<InputStreamSummary>.Failure(SoloFailure.NoRun);
        }

        return await ReadInputsAsync(inputs, subject, item => challenges.Admit(subject, Context.ConnectionId, item), () => challenges.TryTakeNotice(subject));
    }

    private async Task<GhostLapReply<InputStreamSummary>> ReadInputsAsync(
        IAsyncEnumerable<SoloInputItem> inputs, string subject, Func<SoloInputItem, string?> admit, Func<bool> takeNotice)
    {
        if (!Context.Items.TryAdd(StreamOpenKey, true))
        {
            return GhostLapReply<InputStreamSummary>.Failure(SoloFailure.StreamAlreadyOpen);
        }

        long received = 0;
        long rejected = 0;
        var end = "completed";
        var tokens = (double)InputBurst;
        var last = Stopwatch.GetTimestamp();
        var denied = 0;
        try
        {
            await foreach (var item in inputs.WithCancellation(Context.ConnectionAborted))
            {
                received++;
                var now = Stopwatch.GetTimestamp();
                tokens = Math.Min(InputBurst, tokens + Stopwatch.GetElapsedTime(last, now).TotalSeconds * InputsPerSecond);
                last = now;
                string? code;
                if (tokens < 1)
                {
                    code = "rate_limited";
                    if (++denied > InputBurst)
                    {
                        end = "rate_limit_exceeded";
                        log.InputFlood(Context.ConnectionId);
                        Context.Abort();
                        break;
                    }
                }
                else
                {
                    tokens -= 1;
                    denied = 0;
                    code = admit(item);
                }

                if (code is not null)
                {
                    rejected++;
                    if (takeNotice())
                    {
                        await Clients.Caller.InputRejected(new SoloInputRejected(item?.Seq ?? -1, code))
                            .WaitAsync(TimeSpan.FromSeconds(1), Context.ConnectionAborted);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            end = "aborted";
        }
        finally
        {
            Context.Items.Remove(StreamOpenKey);
        }

        return GhostLapReply<InputStreamSummary>.Success(new InputStreamSummary(received, rejected, end));
    }

    /// <summary>The checks every invocation passes: this process is the authority and the guest session is live right now.</summary>
    private async Task<(IActorContext? Actor, string? Failure)> AuthorizeAsync()
    {
        if (guard.AuthorityLost)
        {
            return (null, SoloFailure.Unavailable);
        }

        if (!GhostLapSession.TryRead(Context.User, out var session))
        {
            return (null, SoloFailure.Forbidden);
        }

        var live = await access.IsLiveAsync(session, Context.ConnectionAborted);
        if (live is null)
        {
            return (null, SoloFailure.Unavailable);
        }

        if (!live.Value)
        {
            Context.Abort();
            return (null, SoloFailure.Forbidden);
        }

        return (ClaimsActorContext.From(Context.User), null);
    }
}
