namespace GhostLap.Contracts.Solo;

/// <summary>
/// Wire constants of the Ghost Lap solo recording hub and owner library (Spec 11 task 11.1). A solo run is played on the single
/// Realtime process; the server owns every tick, score and hash. There is no client-reported score, hash or seed.
/// </summary>
public static class GhostLapSoloWire
{
    public const string HubPath = "/hubs/ghostlap-solo";

    /// <summary>GET: the signed-in guest's own completed originals (metadata). GET {route}/{id}: one replay-verified original with its score.</summary>
    public const string OriginalsRoute = "/api/ghostlap/solo/originals";

    /// <summary>Invocation: <see cref="StartSoloRequest"/> to <c>GhostLapReply&lt;SoloRunStarted&gt;</c>.</summary>
    public const string StartRunMethod = "StartRun";

    /// <summary>One client-to-server stream of <see cref="SoloInputItem"/> for the active run.</summary>
    public const string StreamInputsMethod = "StreamInputs";

    /// <summary>Invocation: the run's operation key to <c>GhostLapReply&lt;SoloSaveView&gt;</c>. Idempotent; safe to repeat after a lost response or a refresh.</summary>
    public const string FinishRunMethod = "FinishRun";

    /// <summary>Server-to-client, at most one per tick, latest wins: <see cref="SoloFrame"/>.</summary>
    public const string FrameClientMethod = "Frame";

    /// <summary>Server-to-client, once: the run stopped (<see cref="SoloRunEnded"/>).</summary>
    public const string RunEndedClientMethod = "RunEnded";

    public const int TickRateHz = 30;

    /// <summary>GET: the signed-in guest's own completed Challenge and Fork children (paged). GET {route}/{id}: one verified child. GET {route}/{id}/comparison: same-tick source/child scores.</summary>
    public const string ChildrenRoute = "/api/ghostlap/solo/children";

    /// <summary>Version of the trace/inspect/comparison response shapes. A request that names another version is refused (<see cref="SoloFailure.ContractUnsupported"/>).</summary>
    public const string HistoryContractVersion = "solo-history/1";

    /// <summary>Largest number of ticks one trace request returns; a full round (2,701 states) is ten consecutive chunks.</summary>
    public const int MaxTraceCount = 300;
}

public static class SoloFailure
{
    public const string Forbidden = "forbidden";
    public const string InvalidRequest = "invalid_request";
    public const string Unavailable = "unavailable";
    /// <summary>The key already names a saved original: read it with FinishRun or the library; a new run needs a new key.</summary>
    public const string AlreadySaved = "already_saved";
    /// <summary>The key names a run that was interrupted or never finished. It is not an original; start a new run with a new key.</summary>
    public const string Interrupted = "interrupted";
    /// <summary>The key was used for a different run.</summary>
    public const string Conflict = "conflict";
    /// <summary>This guest already has a live run, or the server's solo capacity is reached.</summary>
    public const string RunActive = "run_active";
    public const string ServerBusy = "server_busy";
    public const string NoRun = "no_run";
    public const string StreamAlreadyOpen = "stream_already_open";
    public const string NotFound = "not_found";
    /// <summary>The request named a history contract version this server does not serve.</summary>
    public const string ContractUnsupported = "contract_unsupported";
}

/// <summary>Start a solo run. <paramref name="OperationKey"/> (1-100 of letters, digits, '-', '_', '.') is the owner-scoped save identity.</summary>
public sealed record StartSoloRequest(string OperationKey, byte MascotId);

/// <summary>The sealed run: the seed is chosen by the server; versions are the pinned solo <c>ghost-v1</c> rules.</summary>
public sealed record SoloRunStarted(
    string OperationKey, string RunId, long Seed, byte MascotId, string RulesVersion, string LayoutVersion, int TickRateHz, int RoundTicks);

/// <summary>One untrusted client input. The slot is always the guest's own (0); there is no position, score or hash field.</summary>
public sealed record SoloInputItem(int Seq, long ClientTick, sbyte MoveX, sbyte MoveY, bool RequestBurst);

/// <summary>The committed authoritative state after <paramref name="Tick"/> (fixed-point coordinates as in the reducer).</summary>
/// <remarks>
/// The first seven fields are the Task 11.1 contract. The trailing fields (Task 11.5) carry the rest of the living state a renderer needs:
/// score components, fixed-point distance, Overlit/Burst state, the uncollected candle ids and this tick's events (state after
/// <c>Tick</c> transitions; <c>Events[i]</c> of tick <c>t</c> is the one-shot effect <c>(t, i)</c>), and <c>AckedInputSeq</c>, the last input
/// sequence the reducer applied (the reconciliation point for client-side prediction; -1 before any input).
/// </remarks>
public sealed record SoloFrame(
    long Tick,
    int XFixed,
    int YFixed,
    int Charge,
    long OverlitDeadlineTick,
    int TotalScore,
    bool Final,
    long DistanceFixed = 0,
    bool Overlit = false,
    int MovementPoints = 0,
    int PickupPoints = 0,
    int PenaltyPoints = 0,
    int Pickups = 0,
    int Bursts = 0,
    int BurstHits = 0,
    sbyte HeldMoveX = 0,
    sbyte HeldMoveY = 0,
    bool BurstLatch = false,
    int CollectedCount = 0,
    int AckedInputSeq = -1,
    string[]? ActiveCandleIds = null,
    GhostEventView[]? Events = null,
    SoloVisibleCandle[]? VisibleCandles = null,
    long Seq = 0,
    SoloMissedTickEvents[]? Missed = null,
    bool MissedTruncated = false);

/// <summary>A Solo candle visible at this authoritative tick, in arena world units. Phase is active or telegraph.</summary>
public sealed record SoloVisibleCandle(string Id, double X, double Y, string Phase);

/// <summary>Ordered reducer events from one coalesced Original tick. The array index identifies the event within its tick.</summary>
public sealed record SoloMissedTickEvents(long Tick, GhostEventView[] Events);

/// <summary>A refused input (stale sequence, bad vector, late/early tick, commit in progress, rate limit). Movement notices are bounded; Burst refusals are always reported.</summary>
/// <remarks>
/// <c>Class</c> separates a benign refusal of movement (<c>benign_movement</c>: a stale/duplicate sequence, a late or future tick, a frozen
/// commit) from a refused one-shot Burst request (<c>burst_rejected</c>, <c>BurstRequested</c> true: the client must resend the press) and
/// from a malformed or rate-limited request (<c>refused</c>). Burst rejections are never throttled away. <c>NextServerTick</c> is the tick
/// the server will accept input for next, so a client can resynchronize.
/// </remarks>
public sealed record SoloInputRejected(
    int Seq, string Code, bool BurstRequested = false, string Class = InputRejectionClasses.Refused, long NextServerTick = 0);

public static class InputRejectionClasses
{
    public const string BenignMovement = "benign_movement";
    public const string BurstRejected = "burst_rejected";
    public const string Refused = "refused";
}

/// <summary><paramref name="Reason"/> is <c>final</c> (all ticks committed; call FinishRun) or <c>interrupted</c> (not an original).</summary>
public sealed record SoloRunEnded(string Reason, string? Code, SoloRunDiagnostics? Diagnostics = null);

/// <summary>
/// Server-side measurements of one challenge or fork round, so authoritative scheduling can be told apart from delivery:
/// <c>CommitInterval*</c> are the intervals between consecutive committed ticks on the server (33.3 ms nominal), <c>MaxScheduleLagMs</c> is the
/// worst commit time behind the ideal 30 Hz schedule, <c>FramesCoalesced</c> counts frames not pushed because the previous push was still in
/// flight (their events travel in the next frame's <c>Missed</c>), and the input counters classify every admission outcome.
/// </summary>
public sealed record SoloRunDiagnostics(
    int Ticks,
    double CommitIntervalP50Ms,
    double CommitIntervalP95Ms,
    double CommitIntervalP99Ms,
    double CommitIntervalMaxMs,
    double MaxScheduleLagMs,
    int FramesCoalesced,
    int InputsAdmitted,
    int BurstsAdmitted,
    int RejectedBenignMovement,
    int RejectedBurst,
    int RejectedOther);

/// <summary>
/// A saved original. <paramref name="Score"/> is present only on a verified read of one original. Version fields are the pinned
/// Timeline key; <paramref name="FinalStateHash"/> is the canonical reducer state hash after tick <paramref name="FinalTick"/>.
/// </summary>
public sealed record SoloOriginalView(
    string Id,
    string OperationKey,
    long Seed,
    byte MascotId,
    string RulesVersion,
    string LayoutVersion,
    string DomainId,
    string ReducerVersion,
    string InputSchemaVersion,
    string StateSchemaVersion,
    string ContextSchemaVersion,
    long FinalTick,
    string FinalStateHash,
    string LastRecordHash,
    int? Score);

/// <summary>
/// Honest save result. <paramref name="Status"/> is <c>Saved</c> only with <paramref name="Original"/>; <c>SavePending</c> means
/// retry FinishRun with the same key; <c>Failed</c> carries a <paramref name="Code"/>.
/// </summary>
public sealed record SoloSaveView(string Status, SoloOriginalView? Original, string? Code);

public static class SoloSaveStatuses
{
    public const string Saved = "Saved";
    public const string SavePending = "SavePending";
    public const string Failed = "Failed";
}

// ---- Spec 11 task 11.3: race the original ghost in a fresh full challenge (additive; the solo recording contract above is unchanged) ----

/// <summary>Wire constants of the challenge methods on the solo hub (<see cref="GhostLapSoloWire.HubPath"/>).</summary>
public static class GhostLapChallengeWire
{
    /// <summary>Invocation: <see cref="PrepareChallengeRequest"/> to <c>GhostLapReply&lt;ChallengePrepared&gt;</c>. Completes when the full ghost trace is verified and held (phase Ready) or fails.</summary>
    public const string PrepareChallengeMethod = "PrepareChallenge";

    /// <summary>Invocation: <see cref="StartChallengeRequest"/> to <c>GhostLapReply&lt;ChallengeStarted&gt;</c>. Only after Ready.</summary>
    public const string StartChallengeMethod = "StartChallenge";

    /// <summary>One client-to-server stream of <see cref="SoloInputItem"/> for the running challenge.</summary>
    public const string StreamChallengeInputsMethod = "StreamChallengeInputs";

    /// <summary>Invocation: the challenge key to <c>GhostLapReply&lt;ChallengeFinishView&gt;</c>. Idempotent after a lost response, refresh or restart.</summary>
    public const string FinishChallengeMethod = "FinishChallenge";

    /// <summary>Invocation: discards the guest's own unfinished challenge (preparing, ready or running). Never a result.</summary>
    public const string CancelChallengeMethod = "CancelChallenge";

    /// <summary>Server-to-client, at most one per tick, latest wins: <see cref="ChallengeFrameView"/>.</summary>
    public const string ChallengeFrameClientMethod = "ChallengeFrame";

    /// <summary>Server-to-client, once: the challenge stopped (<see cref="SoloRunEnded"/>: <c>final</c> or <c>interrupted</c>).</summary>
    public const string ChallengeEndedClientMethod = "ChallengeEnded";

    /// <summary>
    /// Invocation (Task 11.5): <see cref="PrepareForkRequest"/> to <c>GhostLapReply&lt;ChallengePrepared&gt;</c>. Same lifecycle as
    /// <see cref="PrepareChallengeMethod"/> for an arbitrary fork tick 0..2699; then <see cref="StartChallengeMethod"/>,
    /// <see cref="StreamChallengeInputsMethod"/>, <see cref="CancelChallengeMethod"/> and the same server-to-client frames are used.
    /// The first new input is for tick <c>ForkTick + 1</c>.
    /// </summary>
    public const string PrepareForkMethod = "PrepareFork";

    /// <summary>Invocation (Task 11.5): the fork key to <c>GhostLapReply&lt;ChallengeFinishView&gt;</c>. Idempotent; resolves a stored Fork after a refresh or restart.</summary>
    public const string FinishForkMethod = "FinishFork";
}

public static class ChallengeKinds
{
    public const string Challenge = "Challenge";
    public const string Fork = "Fork";
}

public static class ChallengePhases
{
    public const string Ready = "Ready";
    public const string Running = "Running";
}

public static class ChallengeFailure
{
    /// <summary>The guest already has a challenge in progress.</summary>
    public const string ChallengeActive = "challenge_active";
    public const string NotReady = "not_ready";
    public const string PreparationBusy = "preparation_busy";
    public const string NotComplete = "not_complete";
}

public sealed record PrepareChallengeRequest(string OriginalId);

/// <summary>Prepare a Fork of an owned original at <paramref name="ForkTick"/> (0..2699; 2700 is inspect-only). A Fork at tick 0 is still a Fork, not a Challenge.</summary>
public sealed record PrepareForkRequest(string OriginalId, long ForkTick);

/// <summary>The pinned identity a history was recorded under; frames from histories with different values must never be mixed.</summary>
public sealed record SoloPinnedVersions(
    string DomainId, string ReducerVersion, string InputSchemaVersion, string StateSchemaVersion, string ContextSchemaVersion,
    string RulesVersion, string LayoutVersion);

/// <summary>The verified, held ghost: nothing is started yet. The hashes name exactly the original the ghost was prepared from.</summary>
public sealed record ChallengePrepared(
    string OriginalId,
    string Phase,
    long FinalTick,
    string FinalStateHash,
    string RulesVersion,
    string LayoutVersion,
    int TickRateHz,
    string Kind = ChallengeKinds.Challenge,
    long ForkTick = 0,
    long FirstInputTick = 1,
    SoloPinnedVersions? Versions = null);

/// <summary><paramref name="OperationKey"/> (1-90 of letters, digits, '-', '_', '.') is the owner-scoped identity of the challenge child.</summary>
public sealed record StartChallengeRequest(string OperationKey);

/// <summary>One resolved event on a source tick (for later animation and sound): <c>Kind</c> is the reducer event name.</summary>
public sealed record GhostEventView(string Kind, byte Slot, byte OtherSlot, int Value, string CandleId);

/// <summary>
/// The historical source at one tick: read-only data from the verified original (state after <paramref name="Tick"/> transitions), never a
/// living entity. Scores are that tick's running score.
/// </summary>
public sealed record SoloGhostFrame(
    long Tick,
    int XFixed,
    int YFixed,
    int Charge,
    bool Overlit,
    long OverlitDeadlineTick,
    int MovementPoints,
    int PickupPoints,
    int PenaltyPoints,
    int TotalScore,
    int Pickups,
    int Bursts,
    sbyte HeldMoveX,
    sbyte HeldMoveY,
    bool BurstLatch,
    int CollectedCount,
    string[] ActiveCandleIds,
    GhostEventView[] Events,
    string StateHash,
    bool Final,
    long DistanceFixed = 0,
    SoloVisibleCandle[]? VisibleCandles = null);

/// <remarks>
/// <c>Living</c> and <c>Ghost</c> are the states after <c>ForkTick</c> transitions (0 for a Challenge); the first input the server accepts is for
/// <c>FirstInputTick = ForkTick + 1</c> and the round ends at tick <c>RoundTicks</c>. <c>SourceFinalStateHash</c> is the verified final hash of the original the ghost was prepared from.
/// </remarks>
public sealed record ChallengeStarted(
    string OperationKey,
    string ChallengeId,
    string OriginalId,
    long Seed,
    byte MascotId,
    string RulesVersion,
    string LayoutVersion,
    int TickRateHz,
    int RoundTicks,
    SoloFrame Living,
    SoloGhostFrame Ghost,
    string Kind = ChallengeKinds.Challenge,
    long ForkTick = 0,
    long FirstInputTick = 1,
    string SourceFinalStateHash = "",
    SoloPinnedVersions? Versions = null);

/// <summary>The living state and the source ghost at the same committed tick; <paramref name="Delta"/> is living minus source running score.</summary>
/// <remarks>
/// Pushes are latest-wins: a frame is skipped while the previous push is still in flight, so <c>Tick</c> may jump. One-shot effects of a skipped
/// tick are not lost: they are listed in <c>Missed</c> of the next pushed frame (oldest first, only ticks that had events, bounded, with
/// <c>MissedTruncated</c> set if older ones were dropped). An effect is identified by <c>(Tick, index in Events)</c>; a client renders each
/// identity at most once. <c>Seq</c> counts pushed frames; <c>Pacing</c> separates server scheduling from delivery.
/// </remarks>
public sealed record ChallengeFrameView(
    long Tick,
    SoloFrame Living,
    SoloGhostFrame Ghost,
    int Delta,
    bool Final,
    long Seq = 0,
    FramePacing? Pacing = null,
    MissedTickEvents[]? Missed = null,
    bool MissedTruncated = false);

/// <summary>
/// Server timing of one pushed frame. <c>CommitElapsedMs</c> is the time since the round clock started when this tick was committed,
/// <c>ScheduleLagMs</c> how far that is behind the ideal 30 Hz schedule, <c>SendElapsedMs</c> the time the push started and
/// <c>Coalesced</c> the number of ticks skipped since the previously pushed frame. Client arrival gaps minus server send gaps are delivery jitter.
/// </summary>
public sealed record FramePacing(double CommitElapsedMs, double ScheduleLagMs, double SendElapsedMs, int Coalesced);

/// <summary>The one-shot events of one tick whose frame was not pushed.</summary>
public sealed record MissedTickEvents(long Tick, GhostEventView[] Living, GhostEventView[] Ghost);

/// <summary>A verified challenge child: a branch of the original at tick 0 with its own hashes. It is never an original.</summary>
public sealed record ChallengeResultView(
    string ChallengeId,
    string OriginalId,
    string Kind,
    string OperationKey,
    long ForkTick,
    long FinalTick,
    string ChildFinalStateHash,
    string ChildLastRecordHash,
    int LivingScore,
    int SourceScore,
    int Delta,
    string SourceFinalStateHash,
    string SourceLastRecordHash,
    long Seed = 0,
    byte MascotId = 0,
    SoloPinnedVersions? Versions = null,
    string Status = "Completed");

public static class ChallengeFinishStatuses
{
    public const string Completed = "Completed";
    public const string Pending = "Pending";
    public const string Failed = "Failed";
}

/// <summary><c>Completed</c> only with <paramref name="Result"/>; <c>Pending</c> means retry FinishChallenge with the same key; <c>Failed</c> carries a code.</summary>
public sealed record ChallengeFinishView(string Status, ChallengeResultView? Result, string? Code);

// ---- Spec 11 task 11.5: owner-only history reads (inspect, trace), the child catalog and same-tick comparison ----

/// <summary>Identity of the verified original a trace belongs to; repeated on every chunk so frames of different originals or versions are never mixed.</summary>
public sealed record SoloSourceIdentity(string OriginalId, string FinalStateHash, long TerminalTick, SoloPinnedVersions Versions);

/// <summary>
/// <c>GET {OriginalsRoute}/{id}/trace?from=&amp;count=&amp;v=</c>: <c>Count</c> (1-300) consecutive states starting at <c>FromTick</c>, ascending, each the
/// state after exactly that many transitions. <c>NextTick</c> is null once tick <c>Source.TerminalTick</c> is included.
/// </summary>
public sealed record SoloTraceView(string ContractVersion, SoloSourceIdentity Source, long FromTick, int Count, long? NextTick, SoloGhostFrame[] Frames);

/// <summary><c>GET {OriginalsRoute}/{id}/inspect/{tick}</c>: the single verified state at one integer tick 0..2700.</summary>
public sealed record SoloInspectView(string ContractVersion, SoloSourceIdentity Source, SoloGhostFrame Frame);

/// <summary>A completed saved Challenge or Fork child. <c>Score</c> is present only when <c>Verified</c> (the final state was restored and matched the recorded hash).</summary>
public sealed record SoloChildView(
    string Id,
    string SourceId,
    string Kind,
    string OperationKey,
    long ForkTick,
    long FinalTick,
    string FinalStateHash,
    string LastRecordHash,
    int? Score,
    bool Verified,
    string Status,
    long Seed,
    byte MascotId,
    SoloPinnedVersions Versions);

/// <summary><c>GET {ChildrenRoute}?source=&amp;after=&amp;limit=</c>, newest first. <c>NextAfter</c> is the <c>after</c> value of the next page (null on the last page).</summary>
public sealed record SoloChildPageView(SoloChildView[] Items, string? NextAfter);

/// <summary>Score parts of one world at one tick. <c>DistanceFixed</c> is the fixed-point distance behind <c>Movement</c>.</summary>
public sealed record SoloScorePartsView(
    int Movement, int Pickup, int Penalty, int Total, int Pickups, int Bursts, int Charge, bool Overlit, long DistanceFixed);

/// <summary><c>Delta = ChildScore - SourceScore</c> at the same absolute <c>Tick</c>. <c>AfterFork</c> is false through the fork tick, where both worlds are one state.</summary>
public sealed record SoloComparisonPointView(
    long Tick,
    int SourceScore,
    int ChildScore,
    int Delta,
    SoloScorePartsView Source,
    SoloScorePartsView Child,
    bool AfterFork,
    bool Final,
    string SourceStateHash,
    string ChildStateHash);

/// <summary>
/// <c>GET {ChildrenRoute}/{id}/comparison?from=&amp;count=&amp;stride=&amp;v=</c>: up to 300 points at <c>FromTick + i * Stride</c>, ascending.
/// <c>NextTick</c> is the first requested tick after this slice (null once tick 2700 is reachable no further).
/// </summary>
public sealed record SoloComparisonView(
    string ContractVersion,
    string ChildId,
    string SourceId,
    string Kind,
    long ForkTick,
    SoloPinnedVersions Versions,
    long FinalTick,
    string SourceFinalStateHash,
    string ChildFinalStateHash,
    long FromTick,
    int Stride,
    long? NextTick,
    SoloComparisonPointView[] Points);

/// <summary>Owner-only saved-child replay, with explicit parent identity; never an Original.</summary>
public sealed record SoloChildTraceView(
    string ContractVersion, string ChildId, string SourceId, string Kind, long ForkTick,
    string SourceFinalStateHash, string ChildFinalStateHash, SoloPinnedVersions Versions,
    long TerminalTick, long FromTick, int Count, long? NextTick, SoloGhostFrame[] Frames);
