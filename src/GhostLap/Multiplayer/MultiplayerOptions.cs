namespace GhostLap.Multiplayer;

/// <summary>
/// Lobby/room bounds (section <c>GhostLap:Multiplayer</c>). Safety limits, not measured capacity. Every value is range-checked by
/// <see cref="MultiplayerStartup.Validate"/> at host start, so configuration can tighten a bound but never remove it.
/// </summary>
public sealed class MultiplayerOptions
{
    public const string SectionName = "GhostLap:Multiplayer";

    public const int MinCompetitorsFixed = 2;
    public const int MaxCompetitorsCeiling = 4;

    /// <summary>Competitor slots per room (2 to 4). A room with fewer enrolled competitors still starts with 2 or 3.</summary>
    public int MaxCompetitors { get; set; } = 4;

    /// <summary>Read-only spectators per room. Bounded, separate from competitor slots.</summary>
    public int MaxSpectators { get; set; } = 8;

    public int MaxRooms { get; set; } = 256;

    /// <summary>Open connections one member may hold in one room (the controlling tab plus superseded viewer tabs).</summary>
    public int MaxConnectionsPerMember { get; set; } = 3;

    public int CountdownMilliseconds { get; set; } = 3000;

    /// <summary>How long start admission may take after the countdown elapsed before the room returns to the lobby.</summary>
    public int AdmissionTimeoutMilliseconds { get; set; } = 5000;

    /// <summary>The invite code stops resolving this long after the last successful room command. Existing members are unaffected.</summary>
    public int RoomCodeSeconds { get; set; } = 900;

    /// <summary>A room with no successful command for this long closes.</summary>
    public int LobbyIdleSeconds { get; set; } = 1800;

    /// <summary>Absolute room lifetime.</summary>
    public int MaxRoomAgeSeconds { get; set; } = 4 * 3600;

    /// <summary>A competitor whose controlling connection is gone is removed from the lobby after this long.</summary>
    public int DisconnectGraceSeconds { get; set; } = 30;

    /// <summary>A room with no connected competitor closes after this long.</summary>
    public int EmptyRoomSeconds { get; set; } = 60;

    public int RoomCodeLength { get; set; } = 8;

    /// <summary>Invite-code lookups one principal may make per minute (hits and misses count).</summary>
    public int CodeLookupsPerMinute { get; set; } = 20;

    /// <summary>Gameplay items per second one connection may stream (sustained). Ghost Lap's own bound, independent of the shared hub filter.</summary>
    public int InputsPerSecond { get; set; } = 120;

    public int InputBurst { get; set; } = 60;

    /// <summary>Consecutive over-limit items after which the connection is closed.</summary>
    public int InputOverLimitDisconnectAfter { get; set; } = 240;

    /// <summary>Open Ghost Lap hub connections one guest principal may hold.</summary>
    public int MaxConnectionsPerPrincipal { get; set; } = 6;

    /// <summary>Extra browser origins allowed on the Ghost Lap hub. Deliberately separate from <c>Identity:AllowedOrigins</c>.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Opaque rating algorithm id sealed into every countdown (Spec 12 owns its meaning).</summary>
    public string RatingAlgorithmId { get; set; } = MpRulesProfile.InitialRatingAlgorithmId;

    // ---- Task 10.2: authoritative match loop. Safety bounds and initial policy values, NOT measured capacity (task 10.5 measures).

    /// <summary>Queued, not yet applied gameplay items per room. A full mailbox rejects the item (<c>mailbox_full</c>), never blocks.</summary>
    public int MailboxCapacity { get; set; } = 256;

    /// <summary>Admitted inputs one slot may contribute to one tick; more are rejected (<c>tick_input_limit</c>).</summary>
    public int MaxInputsPerSlotPerTick { get; set; } = 8;

    /// <summary>Most logical ticks the scheduler runs back to back in one iteration when it is late (design: initially 3).</summary>
    public int CatchUpTicksPerIteration { get; set; } = 3;

    /// <summary>Backlog (due minus executed ticks) above which the loop counts as late.</summary>
    public int LateAlarmTicks { get; set; } = 2;

    /// <summary>A loop that stays late for longer than this is overloaded and the match is aborted unrated. Deployment setting.</summary>
    public int OverloadSustainedMilliseconds { get; set; } = 2000;

    /// <summary>A backlog above this many ticks aborts the match at once (one stall too long to repair honestly).</summary>
    public int OverloadMaxBacklogTicks { get; set; } = 150;

    /// <summary>How often the durable process generation is re-read to detect retirement by a successor.</summary>
    public int GenerationCheckMilliseconds { get; set; } = 2000;

    /// <summary>An admitted match the room never activates within this time is aborted (orphaned receipt).</summary>
    public int ActivationTimeoutMilliseconds { get; set; } = 30_000;

    /// <summary>Attempts to write a fenced abort before reporting the outcome as unverified.</summary>
    public int AbortAttempts { get; set; } = 5;

    // ---- Task 10.4: result finalization. Initial policy values, not measurements.

    /// <summary>
    /// Attempts to commit a finished match's result (receipt, result row and outbox in one transaction) while this process is the authority. The
    /// match shows <c>pending_finalization</c> meanwhile; after the last failed attempt it is <c>unverified</c> (provisional, unrated), never aborted.
    /// </summary>
    public int FinalizeAttempts { get; set; } = 8;

    /// <summary>First retry delay of the finalizer; doubles per attempt up to <see cref="FinalizeMaxBackoffMilliseconds"/>.</summary>
    public int FinalizeInitialBackoffMilliseconds { get; set; } = 100;

    public int FinalizeMaxBackoffMilliseconds { get; set; } = 2000;

    // ---- Task 10.3: snapshots, per-recipient delivery and hub liveness. Initial policy values, NOT measured capacity (task 10.5).

    /// <summary>Authoritative snapshot every N ticks (design: initially 2, about 15 Hz; up to 30 Hz only after bandwidth measurement). A forfeit, a disconnect and the final tick always publish.</summary>
    public int SnapshotIntervalTicks { get; set; } = 2;

    /// <summary>Ticks of resolved events kept for resync and for gap repair (about 10 s). Older cues are gone; state is never lost.</summary>
    public int EventRetentionTicks { get; set; } = 300;

    /// <summary>
    /// One send to one recipient may take this long. A recipient that exceeds it is cut (competitor: the normal disconnect path with grace;
    /// spectator: dropped). It bounds the memory and the time a congested client can hold, and it never delays another recipient.
    /// </summary>
    public int SendTimeoutMilliseconds { get; set; } = 3000;

    /// <summary>Rejections queued per recipient. Beyond it the newest are dropped and one <c>control_overflow</c> notice reports how many.</summary>
    public int ControlQueueCapacity { get; set; } = 64;

    /// <summary>Server keep-alive ping of the Ghost Lap hub. A client must ping at least twice per <see cref="HubClientTimeoutSeconds"/>.</summary>
    public int HubKeepAliveSeconds { get; set; } = 5;

    /// <summary>
    /// A connection that sent nothing (no item, no ping) for this long is closed by the server. This is the half-open / suspended-tab detection time
    /// and therefore the delay before the 450-tick grace of a vanished competitor starts. Clients must keep a ping interval of at most half of it.
    /// </summary>
    public int HubClientTimeoutSeconds { get; set; } = 15;
}
