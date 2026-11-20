// The semantic seam between a TRANSPORT and the day-play presentation (Spec 13, task 13.4).
//
// PRESENTATION TYPES, NOT SERVER DTOs. A transport adapter turns authoritative frames into PlayFrame and sends ControlIntent upstream;
// everything else in src/play only ever sees these shapes. Two adapters are meant to exist:
//   * MultiplayerTransport.ts: production /hubs/ghostlap. Production Solo remains separate work.
//   * testHostBridge.ts: DEV ONLY, over the existing test-host greybox routes. It carries real authority but is not the production transport.
//
// Nothing here computes a score, collision, pickup or Burst. Identities and event keys come from the adapter; this layer never invents them.
import type { ActorSlot, Charge, ControlIntent } from "../presentation/types.ts";

export type ConnectionState =
  | { readonly kind: "connecting" }
  | { readonly kind: "live" }
  /** Frames stopped arriving or the transport reported a failure. `since` is the local monotonic ms of the last good frame (null: none yet). */
  | { readonly kind: "degraded"; readonly reason: string; readonly sinceMs: number | null }
  | { readonly kind: "ended"; readonly reason: string }
  | { readonly kind: "failed"; readonly reason: string };

export interface PlayerFrame {
  /** Opaque room-scoped member identity from the sealed multiplayer roster, when available. */
  readonly memberId?: string;
  readonly slot: ActorSlot;
  /** Multiplayer only: the server's presence of this slot. "grace" = disconnected, still inside the reconnect window; "forfeited" is final. */
  readonly status?: string;
  readonly connected?: boolean;
  /** Authoritative tick at which a "grace" slot forfeits if it has not returned. */
  readonly graceEndsTick?: number | null;
  /** World units (server fixed-point / 1024, converted by the adapter). */
  readonly x: number;
  readonly y: number;
  readonly charge: Charge;
  readonly overlit: boolean;
  readonly overlitDeadlineTick: number | null;
  readonly totalScore: number;
  readonly movementPoints: number;
  readonly pickupPoints: number;
  readonly penaltyPoints: number;
  readonly pickups: number;
  readonly bursts: number;
  /** Highest input sequence the server has APPLIED for this player, or null when the source does not report one. -1 is a real "none yet". */
  readonly appliedSeq: number | null;
}

export type PlayEventKind = "CandleCollected" | "OverlitStarted" | "ContactDrain" | "BurstManual" | "BurstAuto" | "BurstHit" | "OverlitCancelled" | "RoundFinished" | "Unknown";

export interface PlayEvent {
  /** Stable identity from the source: the server's MatchEvent.Key, or `${frameIdentity}:${tick}:${indexInEvents}` for solo (tick + index). */
  readonly key: string;
  readonly tick: number;
  readonly kind: PlayEventKind;
  /** Server wording of the event, kept for the log when `kind` is "Unknown". */
  readonly raw: string;
  readonly slot: number;
  readonly otherSlot: number;
  readonly value: number;
  readonly candleId: string;
}

export interface CandleFrame {
  readonly id: string;
  readonly x: number;
  readonly y: number;
  /** "telegraph" is the pre-activation warning; only "active" candles can be collected. Both come from the verified plan + server state. */
  readonly phase: "telegraph" | "active";
}

/**
 * The historical ghost at this frame's tick, taken from a VERIFIED saved trace of the source run (never invented, never from a fixture in
 * production). `slot` is the source run's head (the same actual selected head). Absent in day play. Presentation only: the ghost cannot
 * collide, collect, score or trigger an event, and its own recorded pickups are never treated as living ones.
 */
export interface GhostFrame {
  readonly slot: ActorSlot;
  readonly x: number;
  readonly y: number;
}

/**
 * Ghost Challenge / Fork only (13.11): what the verified original did at THIS tick, for the HUD. `sourceScore` is the original's running score
 * at the same absolute tick (never its final score); `delta` is the server's living-minus-source difference. Read-only facts, never a rule.
 */
export interface RaceReadout {
  readonly kind: "Challenge" | "Fork";
  /** 0 for a Challenge; the exact tick the living future branched from for a Fork. */
  readonly forkTick: number;
  readonly sourceScore: number;
  readonly sourceCharge: Charge;
  readonly delta: number;
}

export interface PlayFrame {
  /** Run / match identity (+ generation when the source has one). A frame of another identity resets the session. */
  readonly identity: string;
  readonly tick: number;
  /** Published authoritative snapshot sequence, where available. */
  readonly snapshotSequence?: number;
  readonly tickRate: number;
  readonly roundTicks: number;
  readonly final: boolean;
  /** Local monotonic arrival time (performance.now). A diagnostic, NOT server time. */
  readonly receivedAtMs: number;
  readonly players: readonly PlayerFrame[];
  readonly candles: readonly CandleFrame[];
  /** Night challenge/fork only: the original run's ghost at this tick. */
  readonly ghost?: GhostFrame;
  /** Challenge / Fork only. */
  readonly race?: RaceReadout;
  readonly events: readonly PlayEvent[];
  /** Which slot the local player controls, and whether the server currently lets them. Spectators/superseded tabs/forfeits: canControl=false. */
  readonly you: { readonly slot: ActorSlot | -1; readonly canControl: boolean };
  /** True when the source delivers every committed tick's events. False means events can be coalesced away (the HUD says so). */
  readonly eventsComplete: boolean;
}

/** What the session needs from any transport. */
export interface PlayTransport {
  /** Label shown in the HUD so nobody mistakes a dev bridge for production. */
  readonly label: string;
  readonly production: boolean;
  /** Frames arrive here in order of delivery. Returns an unsubscribe. */
  onFrame(listener: (frame: PlayFrame) => void): () => void;
  onConnection(listener: (state: ConnectionState) => void): () => void;
  /** The transport owns sequence numbers, tick targeting and resend; it gets the CURRENT intent every time it changes. */
  setIntent(intent: ControlIntent, reason: "change" | "neutral"): void;
  /** Reports what the server did with sent input (admitted vs rejected), for honest feedback. May never be called. */
  onInputAnswer(listener: (answer: InputAnswerView) => void): () => void;
  start(signal: AbortSignal): Promise<void>;
  dispose(): void;
}

export interface InputAnswerView {
  readonly seq: number;
  readonly accepted: boolean;
  readonly rejection: string | null;
  readonly burstRequested: boolean;
  /** The transport is re-sending this Burst press after a transient refusal (the C# admission says the client resubmits for the next tick). */
  readonly willRetry?: boolean;
  /** Local monotonic ms when the request was handed to the network. */
  readonly sentAtMs: number;
  readonly answeredAtMs: number;
}
