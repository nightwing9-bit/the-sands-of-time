// Wire shapes of the Ghost Lap greybox (spec 06, task 6.5). Every value is a server answer built from the authoritative C# GameState:
// the browser never computes a position, a pickup, a charge or a score. x/y in world units are for drawing only.
import type { TimelinePosition } from "@sands/contracts";

export interface ArenaRect { x0: number; y0: number; x1: number; y1: number }

export interface Arena {
  rulesVersion: string;
  layoutVersion: string;
  tickRate: number;
  roundTicks: number;
  width: number;
  height: number;
  colliderHalf: number;
  candleHalf: number;
  burstRadius: number;
  maxCharge: number;
  obstacles: ArenaRect[];
}

export interface PlayerView {
  slot: number;
  mascotId: number;
  x: number;
  y: number;
  charge: number;
  overlit: boolean;
  overlitDeadlineTick: number;
  movementPoints: number;
  pickupPoints: number;
  penaltyPoints: number;
  totalScore: number;
  pickups: number;
  bursts: number;
  lastSequence: number;
}

export interface CandleView { id: string; x: number; y: number; activateTick: number; expireTick: number }

export interface WorldEvent { kind: string; slot: number; otherSlot: number; value: number; candleId: string }

export interface WorldView {
  tick: number;
  stateHash: string | null;
  players: PlayerView[];
  activeCandles: CandleView[];
  telegraphCandles: CandleView[];
  collectedCount: number;
  events: WorldEvent[];
  isFinal: boolean;
}

/** The inert source ghost at the same tick as the living world: verified source data, drawn only. */
export interface GhostView { tick: number; x: number; y: number; charge: number; totalScore: number; pickups: number }

export interface AlignedView { tick: number; ghostScore: number; currentScore: number; delta: number; isFinal: boolean; currentStatus: string }

export type LiveStatus = "Ready" | "Running" | "Completing" | "Complete" | "Failed" | "Abandoned";

export interface LiveFrame {
  sessionId: string;
  kind: "original" | "branch";
  status: LiveStatus;
  error: string | null;
  sourceId: string | null;
  forkTick: number | null;
  tickRate: number;
  world: WorldView;
  ghost: GhostView | null;
  aligned: AlignedView | null;
  completedId: string | null;
  finalStateHash: string | null;
}

export interface InputAnswer { accepted: boolean; rejection: string | null; appliesToTick: number | null; nextTick: number; lastSequence: number }

export interface OriginalDescriptor {
  timelineId: string;
  sourceId: string;
  bounds: { start: TimelinePosition; end: TimelinePosition };
  readOnly: boolean;
  markers: { position: TimelinePosition; label: string }[];
  historyStatus: string;
  seed: number;
  mascotId: number;
  finalScore: number;
  finalStateHash: string;
  arena: Arena;
}

export interface Intent { moveX: -1 | 0 | 1; moveY: -1 | 0 | 1; requestBurst: boolean }
