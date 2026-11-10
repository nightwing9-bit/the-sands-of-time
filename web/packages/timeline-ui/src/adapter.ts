// Shared browser Timeline adapter (docs/architecture/03-contracts.md, "Browser Timeline adapter v1").
// Domain adapters live in each app. This package holds no game or bank rules.
import type { TimelinePosition } from '@sands/contracts';

/**
 * A server-verified preview for one position. `T` is the domain's own preview state; the scrubber never inspects it.
 * `position` echoes the position the server verified, so a response can be checked against the request.
 */
export interface VerifiedPreview<T = unknown> {
  position: TimelinePosition;
  state: T;
}

/** Optional extras passed as a second argument. An adapter that ignores them still satisfies the frozen contract. */
export interface SeekOptions {
  /** Aborted when a newer seek replaces this one or the component unmounts. */
  signal: AbortSignal;
  /** Monotonic per-scrubber operation ID. Only the newest ID may paint. */
  operationId: number;
}

export interface TimelineMarker {
  position: TimelinePosition;
  label: string;
}

export interface TimelineAdapter<TPreview = unknown> {
  sourceId: string;
  branchId?: string;
  bounds: { start: TimelinePosition; end: TimelinePosition };
  readOnly: boolean;
  getLabel(position: TimelinePosition): string;
  getMarkers(): TimelineMarker[];
  seek(position: TimelinePosition, options?: SeekOptions): Promise<VerifiedPreview<TPreview>>;
  fork(position: TimelinePosition, options: unknown, idempotencyKey: string): Promise<{ branchId: string }>;
}
