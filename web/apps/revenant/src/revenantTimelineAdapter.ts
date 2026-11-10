// Revenant's thin Timeline adapter (spec 04, task 4.3): business date/time labels over the shared TimelineAdapter contract.
// It holds no finance rules. Every preview and fork is a server answer; a failure is thrown as `{ failure }` for the scrubber.
// Transport is behind RevenantTimelineWire (./revenantTimelineWire.ts); today it talks to the test-only Timeline host, see the note there.
import { type TimelinePosition } from "@sands/contracts";
import type { SeekOptions, TimelineAdapter, TimelineMarker, VerifiedPreview } from "@sands/timeline-ui";
import type { RevenantTimelineWire } from "./revenantTimelineWire.ts";

/** Server-verified Revenant state at a position. Only what the shell displays. */
export interface RevenantPreview {
  businessTime: string;
  stateHash?: string;
}
export interface RevenantTimelineDescriptor {
  sourceId: string;
  branchId?: string;
  bounds: { start: TimelinePosition; end: TimelinePosition };
  readOnly: boolean;
  markers: TimelineMarker[];
}
export interface RevenantAdapterOptions {
  timelineId: string;
  descriptor: RevenantTimelineDescriptor;
  wire: RevenantTimelineWire;
}

const dateTime = new Intl.DateTimeFormat("en-GB", { dateStyle: "medium", timeStyle: "short", timeZone: "UTC" });
export function formatBusinessTime(iso: string): string | null {
  const t = Date.parse(iso);
  return Number.isNaN(t) ? null : `${dateTime.format(new Date(t))} UTC`;
}

export function createRevenantAdapter(options: RevenantAdapterOptions): TimelineAdapter<RevenantPreview> {
  const { descriptor, wire, timelineId } = options;
  // Business times the server has confirmed (previews) so labels never invent a date for an unverified position.
  const known = new Map<TimelinePosition, string>();
  return {
    sourceId: descriptor.sourceId,
    branchId: descriptor.branchId,
    bounds: descriptor.bounds,
    readOnly: descriptor.readOnly,
    getLabel(position) {
      const time = known.get(position);
      return `Event ${position}${time ? ` · ${time}` : ""}`;
    },
    getMarkers: () => descriptor.markers,
    async seek(position, seekOptions?: SeekOptions): Promise<VerifiedPreview<RevenantPreview>> {
      const answer = await wire.restore(timelineId, position, seekOptions?.signal);
      const label = formatBusinessTime(answer.state.businessTime);
      if (label === null) throw { failure: { kind: "malformed", message: "The preview had no valid business time." } };
      known.set(answer.position, label);
      return answer;
    },
    fork: (position, forkOptions, idempotencyKey) => wire.fork(timelineId, position, idempotencyKey, forkOptions),
  };
}
