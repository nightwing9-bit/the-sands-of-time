using System.Security.Cryptography;
using GhostLap.Rules;
using GhostLap.Simulation;
using GhostLap.Timeline;
using Marrow.Kernel.Canonical;
using Marrow.Kernel.Results;
using Marrow.Kernel.Security;
using Marrow.Timeline;

namespace GhostLap.Solo;

/// <summary>
/// Ghost Lap's solo-original handler over the one shared Marrow Timeline (public <see cref="ITimelineEngine{TState,TInput,TContext}"/>
/// only) and the Ghost Lap-owned <c>ghostlap.solo_originals</c> row. The owner is always the verified
/// <see cref="IActorContext"/>; every read goes through the Spec 03 <see cref="IResourceAuthorizer"/> (composed with
/// <see cref="SoloOwnershipLookup"/>), and a non-owner gets the same <see cref="SoloErrorCode.NotFound"/> as an unknown id.
/// Source histories are only appended while recording and only read afterwards; nothing here rewrites them.
/// Spec 11 adds catalog/listing on top of this; the state/input codec stays the single <c>ghost-v1</c> one.
/// </summary>
public sealed class SoloOriginals(
    ITimelineEngine<GameState, GameInput, GameContext> engine,
    SoloOriginalStore store,
    IResourceAuthorizer authorizer)
{
    /// <summary>Upper bound for client-chosen operation keys, so a request cannot grow keys without limit.</summary>
    public const int MaxOperationKeyLength = 100;

    private readonly ITimelineEngine<GameState, GameInput, GameContext> _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly SoloOriginalStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IResourceAuthorizer _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));

    /// <summary>
    /// Starts (or, with the same operation key, resumes) the actor's solo recording for <paramref name="seed"/>: commits
    /// the Timeline source with its complete tick-0 checkpoint and pinned context, registers the owned row, and returns a
    /// recorder positioned at the last committed tick. A retry after a crash continues from what the database holds.
    /// </summary>
    public Task<Result<SoloRecording, SoloError>> StartAsync(
        IActorContext actor, long seed, byte mascotId, string operationKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated || string.IsNullOrEmpty(actor.SubjectId))
        {
            return Task.FromResult(SoloResult.Fail<SoloRecording>(SoloErrorCode.Unauthenticated, "Recording requires a verified actor."));
        }

        if (!IsValidOperationKey(operationKey))
        {
            return Task.FromResult(SoloResult.Fail<SoloRecording>(SoloErrorCode.InvalidRequest,
                $"Operation key must be 1-{MaxOperationKeyLength} characters of letters, digits, '-', '_' or '.'."));
        }

        if (mascotId >= GameRulesV1.MascotCount)
        {
            return Task.FromResult(SoloResult.Fail<SoloRecording>(SoloErrorCode.InvalidRequest,
                $"Mascot must be 0-{GameRulesV1.MascotCount - 1}."));
        }

        var context = GameContext.Create(seed, 1, [mascotId]);
        return SoloResult.GuardAsync(async () =>
        {
            var initial = GameState.Initial(context);
            var request = new CreateSourceRequest<GameState, GameContext>(
                actor.SubjectId, GhostLapTimelineVersions.V1, initial, context, RulesHash, SeedHash(context));
            var created = await _engine.CreateSourceAsync(request, SoloRecording.SourceKey(operationKey), ct).ConfigureAwait(false);
            if (!created.TryGetValue(out var id))
            {
                return SoloResult.Fail<SoloRecording>(created.Error);
            }

            var registered = await _store.RegisterAsync(new SoloOriginalRecord(
                id, actor.SubjectId, actor.PrincipalKind, actor.TenantId, operationKey, context.RulesVersion,
                context.LayoutVersion, seed, mascotId, SoloOriginalStatus.Recording), ct).ConfigureAwait(false);
            if (!registered.TryGetValue(out var row))
            {
                return SoloResult.Fail<SoloRecording>(registered.Error);
            }

            // Resume point: the verified prefix the database holds (tick 0 for a new recording).
            var current = await _engine.ReplayAsync(id, ct).ConfigureAwait(false);
            if (!current.TryGetValue(out var replay))
            {
                return SoloResult.Fail<SoloRecording>(current.Error);
            }

            return replay.Status == HistoryStatus.Failed
                ? SoloResult.Fail<SoloRecording>(SoloErrorCode.Conflict, "This recording failed verification and cannot continue.")
                : SoloResult.Ok(new SoloRecording(_engine, _store, row, context, replay.FinalState));
        }, ct);
    }

    /// <summary>
    /// Opens the actor's completed original after a refresh or restart: replays the whole history from the database and
    /// requires the final state hash and last record hash to equal the facts stored at completion.
    /// </summary>
    public Task<Result<SoloOriginal, SoloError>> OpenAsync(IActorContext actor, TimelineId timeline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return SoloResult.GuardAsync(async () =>
        {
            var row = await ReadableOriginalAsync(actor, timeline, ct).ConfigureAwait(false);
            if (!row.TryGetValue(out var original))
            {
                return SoloResult.Fail<SoloOriginal>(row.Error);
            }

            var replayed = await _engine.ReplayAsync(timeline, ct).ConfigureAwait(false);
            if (!replayed.TryGetValue(out var replay))
            {
                return SoloResult.Fail<SoloOriginal>(replayed.Error);
            }

            if (replay.Status != HistoryStatus.Verified || replay.FinalPosition != original.FinalTick
                || replay.FinalStateHash != original.FinalStateHash || replay.LastRecordHash != original.LastRecordHash
                || !replay.FinalState.IsFinal)
            {
                return SoloResult.Fail<SoloOriginal>(SoloErrorCode.VerificationFailed,
                    "The stored history no longer reproduces the hashes recorded at completion.");
            }

            return SoloResult.Ok(new SoloOriginal(timeline, original.Seed, original.MascotId, replay.FinalPosition,
                replay.FinalStateHash, replay.LastRecordHash, replay.FinalState));
        }, ct);
    }

    /// <summary>
    /// The complete verified state of the actor's original after exactly ticks 1..<paramref name="tick"/>
    /// (0 is the initial state): position, charge and Overlit deadline, score components and fixed-point distance,
    /// cooldowns, effective-input state, collected/active candles. The future schedule is the pinned context's plan.
    /// </summary>
    public Task<Result<VerifiedState<GameState>, SoloError>> RestoreAsync(
        IActorContext actor, TimelineId timeline, long tick, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return SoloResult.GuardAsync(async () =>
        {
            var row = await ReadableOriginalAsync(actor, timeline, ct).ConfigureAwait(false);
            if (row.IsFailure)
            {
                return SoloResult.Fail<VerifiedState<GameState>>(row.Error);
            }

            var restored = await _engine.RestoreAsync(timeline, tick, ct).ConfigureAwait(false);
            if (!restored.TryGetValue(out var state))
            {
                return SoloResult.Fail<VerifiedState<GameState>>(restored.Error);
            }

            return state.HistoryStatus == HistoryStatus.Verified
                ? SoloResult.Ok(state)
                : SoloResult.Fail<VerifiedState<GameState>>(SoloErrorCode.VerificationFailed, "The history is not Verified.");
        }, ct);
    }

    /// <summary>Rules identity pinned on every Ghost Lap Timeline source (inherited by later forks).</summary>
    public static string RulesHash => $"{GameRulesV1.RulesVersion}+{GameRulesV1.LayoutVersion}";

    /// <summary>SHA-256 over (rules, layout, seed): names the candle schedule a history was played on.</summary>
    public static string SeedHash(GameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bytes = new CanonicalWriter()
            .WriteString(context.RulesVersion).WriteString(context.LayoutVersion).WriteInt64(context.Seed).ToArray();
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private Task<Result<SoloOriginalRecord, SoloError>> ReadableOriginalAsync(
        IActorContext actor, TimelineId timeline, CancellationToken ct) => ReadableOriginalAsync(_authorizer, _store, actor, timeline, ct);

    /// <summary>The actor's own Complete original row: owner policy first, then the row; NotFound for anything not theirs.</summary>
    internal static async Task<Result<SoloOriginalRecord, SoloError>> ReadableOriginalAsync(
        IResourceAuthorizer authorizer, SoloOriginalStore store, IActorContext actor, TimelineId timeline, CancellationToken ct)
    {
        var decision = await authorizer.AuthorizeAsync(actor, SoloOwnershipLookup.ResourceOf(timeline), ResourceOperation.Read, ct)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return SoloResult.Fail<SoloOriginalRecord>(SoloErrorCode.NotFound, "No such solo original.");
        }

        var row = await store.FindAsync(timeline, ct).ConfigureAwait(false);
        if (!row.TryGetValue(out var original))
        {
            return row;
        }

        return original.Status == SoloOriginalStatus.Complete
            ? row
            : SoloResult.Fail<SoloOriginalRecord>(SoloErrorCode.NotComplete,
                "This run was not completed and verified, so it is not an original.");
    }

    /// <summary>The pinned context of an original, rebuilt from its persisted row (solo: one player, the chosen mascot).</summary>
    internal static GameContext ContextOf(SoloOriginalRecord original) => GameContext.Create(original.Seed, 1, [original.MascotId]);

    internal static bool IsValidOperationKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxOperationKeyLength
        && key.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.');
}
