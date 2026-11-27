// Wire shapes checked against GhostLap.Contracts/Multiplayer and GhostLapPresentation.cs.
export interface RoomMemberWire { memberId: string; role: string; ready: boolean; connected: boolean; isHost: boolean; slot: number | null }
export interface SealedRoundWire {
  matchId: string; authorityGeneration: string; layoutId: string; layoutFingerprint: string; targetTicks: number; countdownMs?: number;
  slots: { slot: number; memberId: string }[];
}
export interface RoomView {
  roomId: string; version: number; phase: string; matchStatus: string | null;
  maxCompetitors?: number; maxSpectators?: number;
  /** Authoritative remaining countdown at the moment the server built this view. */
  countdownRemainingMs?: number;
  /** Machine-readable reason of the last room transition (RoomNotices). */
  notice?: string | null;
  members: RoomMemberWire[];
  round: SealedRoundWire | null;
}
export interface JoinedRoom { view: RoomView; memberId: string; role: string; controllerEpoch: number; roomCode: string }
export interface RoomCommandResult { view: RoomView; detail: string | null }
export interface GameplayInput { matchId: string; controllerEpoch: number; clientTick: number; seq: number; moveX: number; moveY: number; requestBurst: boolean }
export interface SnapshotFrame {
  snapshot: { matchId: string; generationId: string; rulesId: string; layoutId: string; tick: number; sequence: number; isFinal: boolean;
    players: { slot: number; status: string; connected: boolean; xFixed: number; yFixed: number; moveX: number; moveY: number; charge: number;
      overlitDeadlineTick: number; pickups: number; bursts: number; burstHits: number; movementPoints: number; pickupPoints: number;
      penaltyPoints: number; totalScore: number; lastSequence: number; forfeitTick?: number; graceEndsTick?: number }[]; activeCandleIds: string[] };
  lifecycleVersion: number; matchStatus: string; isResync: boolean;
  you: { role: string; slot: number; controllerEpoch: number; canControl: boolean; appliedSeq: number };
  events: { truncated: boolean; items: { key: number; tick: number; index: number; kind: string; slot: number; otherSlot: number; value: number; candleId: string | null }[] };
  visibleCandles: { id: string; x: number; y: number; phase: "active" | "telegraph" }[] | null;
}

/** One row of the durable result as members see it (GhostLapResultContracts.cs MatchPlayerResultView). */
export interface MatchPlayerResultWire {
  slot: number; memberId: string; outcome: string; forfeitTick: number; competitivePlacement: number; totalScore: number;
  movementPoints: number; pickupPoints: number; penaltyPoints: number; pickups: number; bursts: number; burstHits: number;
  timesHitByBurst: number; contactDrainsSuffered: number;
}
/** GetMatchResult reply (MatchOutcomeView). Decision: "finalized" | "aborted" | "pending". Placements exist only when finalized. */
export interface MatchOutcomeWire {
  matchId: string; decision: string; abortReason: string | null; resultDigest: string | null; ratedEligible: boolean;
  eligibilityReason: string | null; ratingAlgorithmId: string | null; players: MatchPlayerResultWire[];
}

/** A hub method that answered { ok: false }. `code` is the server's own failure code (GhostLapFailure). */
export class HubCallError extends Error {
  readonly method: string;
  readonly code: string;
  constructor(method: string, code: string) {
    super(`${method}: ${code}`);
    this.name = "HubCallError";
    this.method = method;
    this.code = code;
  }
}

const FAILURE_TEXT: Readonly<Record<string, string>> = {
  invalid_request: "That request wasn't valid.",
  room_unavailable: "That room doesn't exist, or it has closed.",
  already_in_room: "You're already in a room from another tab or window.",
  room_full: "That room already has four players.",
  spectators_full: "That room has no spectator seats left.",
  rooms_full: "The server can't open another room right now. Try again shortly.",
  join_closed: "That room isn't accepting new players right now.",
  role_conflict: "You're already in that room in a different role.",
  too_many_connections: "Too many connections from this browser. Close another tab and retry.",
  lookup_limited: "Too many attempts. Wait a moment, then try again.",
  rate_limited: "Too many requests. Wait a moment, then try again.",
  not_controller: "This tab isn't controlling your seat any more.",
  spectator_forbidden: "Spectators can't do that.",
  not_host: "Only the host can do that.",
  wrong_phase: "That isn't possible in the room's current state.",
  roster_invalid: "A match needs two to four players.",
  not_ready: "Everyone has to be ready first.",
  not_connected: "Someone in the room is disconnected.",
  host_target_invalid: "That player can't be host.",
  stale_version: "The room changed while you were acting. Check it and try again.",
  start_committing: "The match is already starting.",
  controller_superseded: "Another tab took over this seat.",
  no_running_match: "There is no running match to join.",
  wrong_match: "That belongs to a different match.",
  finalization_pending: "The server is still recording the result. Try again in a moment.",
  result_not_found: "This result isn't available to you.",
  forbidden: "The game server refused this session. Reload the page to start a fresh one.",
  unavailable: "The game server is unavailable right now.",
};

/** Plain-language text for a failure. Codes are the server's; unknown codes are shown as such rather than hidden behind a generic apology. */
export function failureText(error: unknown): string {
  if (error instanceof HubCallError) return FAILURE_TEXT[error.code] ?? `The server answered "${error.code}".`;
  if (typeof error === "string") return error;
  if (error instanceof Error) return error.message.startsWith("Error: ") ? error.message.slice(7) : error.message;
  return String(error);
}
