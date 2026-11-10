// Ghost Lap's thin Timeline adapter (spec 04, task 4.3): tick and mm:ss labels over the shared TimelineAdapter contract.
// It holds no game rules and never advances a tick itself. Every preview and fork is a server answer.
// Transport is behind GhostLapTimelineWire (./ghostLapTimelineWire.ts); today it talks to the test-only Timeline host, see the note there.
import { parsePosition, type TimelinePosition } from "@sands/contracts";
import type { SeekOptions, TimelineAdapter, TimelineMarker, VerifiedPreview } from "@sands/timeline-ui";
import type { GhostLapTimelineWire } from "./ghostLapTimelineWire.ts";

/** Server-verified Ghost Lap state at a tick. Only what the shell displays. */
export interface GhostLapPreview {
  tick: number;
  stateHash?: string;
}
export interface GhostLapTimelineDescriptor {
  sourceId: string;
  branchId?: string;
  bounds: { start: TimelinePosition; end: TimelinePosition };
  readOnly: boolean;
  markers: TimelineMarker[];
}
export interface GhostLapAdapterOptions {
  timelineId: string;
  descriptor: GhostLapTimelineDescriptor;
  wire: GhostLapTimelineWire;
  /** Ticks per second of the rules version in play. ghost-v1 initial target is 30 (spec 06); not a measured value. */
  ticksPerSecond?: number;
}

const DEFAULT_TICKS_PER_SECOND = 30;
export function formatTick(position: TimelinePosition, ticksPerSecond: number): string {
  const tick = parsePosition(position);
  if (tick === null) return position;
  const seconds = tick / BigInt(ticksPerSecond);
  const mm = (seconds / 60n).toString().padStart(2, "0");
  const ss = (seconds % 60n).toString().padStart(2, "0");
  return `Tick ${position} · ${mm}:${ss}`;
}

export function createGhostLapAdapter(options: GhostLapAdapterOptions): TimelineAdapter<GhostLapPreview> {
  const { descriptor, wire, timelineId } = options;
  const tps = options.ticksPerSecond ?? DEFAULT_TICKS_PER_SECOND;
  return {
    sourceId: descriptor.sourceId,
    branchId: descriptor.branchId,
    bounds: descriptor.bounds,
    readOnly: descriptor.readOnly,
    getLabel: (position) => formatTick(position, tps),
    getMarkers: () => descriptor.markers,
    seek: (position, seekOptions?: SeekOptions): Promise<VerifiedPreview<GhostLapPreview>> => wire.restore(timelineId, position, seekOptions?.signal),
    fork: (position, forkOptions, idempotencyKey) => wire.fork(timelineId, position, idempotencyKey, forkOptions),
  };
}
