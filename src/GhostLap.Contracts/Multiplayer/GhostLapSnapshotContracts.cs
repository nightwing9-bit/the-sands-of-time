namespace GhostLap.Contracts.Multiplayer;

/// <summary>Semantic kinds of resolved authoritative gameplay events (task 10.3). Cues for rendering and sound, never inputs and never state.</summary>
public static class MatchEventKinds
{
    public const string CandleCollected = "candle_collected";
    public const string OverlitStarted = "overlit_started";
    public const string ContactDrain = "contact_drain";
    public const string BurstManual = "burst_manual";
    public const string BurstAuto = "burst_auto";
    public const string BurstHit = "burst_hit";
    public const string OverlitCancelled = "overlit_cancelled";
    public const string RoundFinished = "round_finished";

    /// <summary>Phase 0 of <c>ghost-mp-v1</c>: the slot's 450-tick grace ended without a controller. <c>Value</c> = its frozen total score.</summary>
    public const string Forfeited = "forfeited";
}

/// <summary>Per-slot participation status in a snapshot.</summary>
public static class SlotStatuses
{
    /// <summary>Taking part with a controlling connection.</summary>
    public const string Active = "active";

    /// <summary>No controlling connection. Neutral input is applied, the slot is still a normal target and collector until <see cref="PlayerSnapshot.GraceEndsTick"/>.</summary>
    public const string Grace = "grace";

    /// <summary>Forfeited: frozen, no actor or target of any phase. A later connection is spectator-only for this match.</summary>
    public const string Forfeited = "forfeited";
}

/// <summary>
/// One resolved authoritative event. Its identity is <c>(MatchId, GenerationId, Tick, Index)</c> where the match and generation are those of
/// the carrying frame. <see cref="Index"/> is the event's position in that tick's resolved order (a forfeit first, then the ghost-v1 phase
/// events), so the key is stable across retransmission: a client that has applied a key applies it never again.
/// </summary>
public sealed record MatchEvent(long Tick, int Index, string Kind, int Slot, int OtherSlot, int Value, string? CandleId)
{
    /// <summary>Total order of events inside one match. A tick resolves far fewer than 1024 events.</summary>
    public long Key => (Tick * 1024) + Index;
}

/// <summary>
/// One slot of an authoritative snapshot. Positions are fixed-point units (1 world unit = 1024). <see cref="MoveX"/>/<see cref="MoveY"/> is the
/// effective direction that moved the slot on this tick (the client's prediction basis), never a position claim.
/// </summary>
public sealed record PlayerSnapshot(
    int Slot,
    string Status,
    bool Connected,
    int XFixed,
    int YFixed,
    int MoveX,
    int MoveY,
    int Charge,
    long OverlitDeadlineTick,
    int Pickups,
    int Bursts,
    int BurstHits,
    int MovementPoints,
    int PickupPoints,
    int PenaltyPoints,
    int TotalScore,
    int LastSequence,
    long ForfeitTick,
    long GraceEndsTick);

/// <summary>
/// The shared, immutable authoritative state at the end of <see cref="Tick"/>. <see cref="Sequence"/> counts published snapshots of this match
/// (0 is the tick-0 state); gaps are normal because stale snapshots are coalesced for slow recipients.
/// </summary>
public sealed record MatchSnapshot(
    string MatchId,
    string GenerationId,
    string RulesId,
    string LayoutId,
    long Tick,
    long Sequence,
    bool IsFinal,
    IReadOnlyList<PlayerSnapshot> Players,
    IReadOnlyList<string> ActiveCandleIds);

/// <summary>
/// What is true of THIS connection. <see cref="AppliedSeq"/> is the sequence floor of its slot as of the newest resolved tick (not older than the
/// snapshot's <see cref="PlayerSnapshot.LastSequence"/>): the highest sequence the authoritative loop applied (a server neutral from a disconnect
/// consumes one), so a new controller must send above it. <see cref="CanControl"/> is false for spectators,
/// superseded tabs and forfeited slots.
/// </summary>
public sealed record RecipientView(string Role, int Slot, long ControllerEpoch, bool CanControl, int AppliedSeq);

/// <summary>
/// Events with <c>FromTick &lt;= Tick &lt;= ThroughTick</c> not yet sent to this recipient (or, in a resync, the retained tail).
/// <see cref="Truncated"/> means events older than <see cref="FromTick"/> that this recipient still needed were no longer retained; the frame is then a
/// resync baseline and the client must not assume it saw every cue.
/// </summary>
public sealed record EventBatch(long FromTick, long ThroughTick, bool Truncated, IReadOnlyList<MatchEvent> Items);

/// <summary>
/// One delivery to one connection. <see cref="LifecycleVersion"/>/<see cref="MatchStatus"/> are the match lifecycle at send time (a counter that
/// only rises), so a snapshot can never resurrect a match the client already knows is over. <see cref="IsResync"/> frames replace the client's baseline.
/// </summary>
public sealed record SnapshotFrame(
    MatchSnapshot Snapshot,
    long LifecycleVersion,
    string MatchStatus,
    RecipientView You,
    EventBatch Events,
    bool IsResync,
    IReadOnlyList<VisibleCandleView>? VisibleCandles = null);

/// <summary>Only currently active or legitimately telegraphed candles, in world units. No future schedule.</summary>
public sealed record VisibleCandleView(string Id, int X, int Y, string Phase);

/// <summary>Reliable control feedback that is neither a rejection nor state (<see cref="MatchControlCodes"/>).</summary>
public sealed record MatchControlNotice(string MatchId, string GenerationId, long LifecycleVersion, string Code, string? Detail, long Tick);

public static class MatchControlCodes
{
    /// <summary>This connection's slot was forfeited at <c>Tick</c>. From now on it can only watch.</summary>
    public const string Forfeited = "forfeited";

    /// <summary>Rejections for this connection were dropped because its reliable queue was full (<c>Detail</c> = how many). Resync if unsure.</summary>
    public const string ControlOverflow = "control_overflow";
}

/// <summary>Immutable collision-aligned arena projection. Coordinates are world units.</summary>
public sealed record ArenaObstacleView(int X0, int Y0, int X1, int Y1);
public sealed record ArenaPresentationView(string LayoutId, string LayoutFingerprint, int Width, int Height,
    IReadOnlyList<ArenaObstacleView> Obstacles);
