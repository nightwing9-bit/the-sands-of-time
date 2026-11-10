// Revenant slice adapter over the one shared TimelineAdapter contract (spec 05, task 5.4). It holds no finance rule: every preview is a
// server answer from a Marrow restore, and a fork is the server's fork + continue + compare under the policy chosen in `options`.
import type { TimelinePosition } from "@sands/contracts";
import type { SeekOptions, TimelineAdapter, VerifiedPreview } from "@sands/timeline-ui";
import { formatBusinessTime } from "../revenantTimelineAdapter.ts";
import type { SliceWire } from "./sliceWire.ts";
import type { BranchOptions, RunDescriptor, SliceState } from "./types.ts";

export function createSliceAdapter(descriptor: RunDescriptor, wire: SliceWire): TimelineAdapter<SliceState> {
  // Business times the server confirmed, so a label never invents a time for a position it has not verified.
  const known = new Map<TimelinePosition, string>();
  for (const event of descriptor.events) {
    const label = formatBusinessTime(event.businessTime);
    if (label) known.set(event.position, label);
  }
  return {
    sourceId: descriptor.sourceId,
    branchId: descriptor.branchId ?? undefined,
    bounds: descriptor.bounds,
    readOnly: descriptor.readOnly,
    getLabel(position) {
      const time = known.get(position);
      return `Event ${position}${time ? ` · ${time}` : ""}`;
    },
    getMarkers: () => descriptor.markers,
    async seek(position, options?: SeekOptions): Promise<VerifiedPreview<SliceState>> {
      const answer = await wire.restore(descriptor.timelineId, position, options?.signal);
      return { position: answer.position, state: answer.state };
    },
    fork: (position, options, idempotencyKey) => wire.createBranch(descriptor.timelineId, position, idempotencyKey, options as BranchOptions),
  };
}
