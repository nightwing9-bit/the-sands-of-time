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
}

/// <summary>Start a solo run. <paramref name="OperationKey"/> (1-100 of letters, digits, '-', '_', '.') is the owner-scoped save identity.</summary>
public sealed record StartSoloRequest(string OperationKey, byte MascotId);

/// <summary>The sealed run: the seed is chosen by the server; versions are the pinned solo <c>ghost-v1</c> rules.</summary>
public sealed record SoloRunStarted(
    string OperationKey, string RunId, long Seed, byte MascotId, string RulesVersion, string LayoutVersion, int TickRateHz, int RoundTicks);

/// <summary>One untrusted client input. The slot is always the guest's own (0); there is no position, score or hash field.</summary>
public sealed record SoloInputItem(int Seq, long ClientTick, sbyte MoveX, sbyte MoveY, bool RequestBurst);

/// <summary>The committed authoritative state after <paramref name="Tick"/> (fixed-point coordinates as in the reducer).</summary>
public sealed record SoloFrame(
    long Tick, int XFixed, int YFixed, int Charge, long OverlitDeadlineTick, int TotalScore, bool Final);

/// <summary>A refused input (stale sequence, bad vector, late/early tick, commit in progress, rate limit). Bounded: at most ~20 per second.</summary>
public sealed record SoloInputRejected(int Seq, string Code);

/// <summary><paramref name="Reason"/> is <c>final</c> (all ticks committed; call FinishRun) or <c>interrupted</c> (not an original).</summary>
public sealed record SoloRunEnded(string Reason, string? Code);

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

/// <summary>The verified, held ghost: nothing is started yet. The hashes name exactly the original the ghost was prepared from.</summary>
public sealed record ChallengePrepared(
    string OriginalId, string Phase, long FinalTick, string FinalStateHash, string RulesVersion, string LayoutVersion, int TickRateHz);

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
    bool Final);

public sealed record ChallengeStarted(
    string OperationKey, string ChallengeId, string OriginalId, long Seed, byte MascotId, string RulesVersion, string LayoutVersion,
    int TickRateHz, int RoundTicks, SoloFrame Living, SoloGhostFrame Ghost);

/// <summary>The living state and the source ghost at the same committed tick; <paramref name="Delta"/> is living minus source running score.</summary>
public sealed record ChallengeFrameView(long Tick, SoloFrame Living, SoloGhostFrame Ghost, int Delta, bool Final);

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
    string SourceLastRecordHash);

public static class ChallengeFinishStatuses
{
    public const string Completed = "Completed";
    public const string Pending = "Pending";
    public const string Failed = "Failed";
}

/// <summary><c>Completed</c> only with <paramref name="Result"/>; <c>Pending</c> means retry FinishChallenge with the same key; <c>Failed</c> carries a code.</summary>
public sealed record ChallengeFinishView(string Status, ChallengeResultView? Result, string? Code);
