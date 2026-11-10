// Ghost Lap original-run adapter over the one shared TimelineAdapter contract (spec 06, task 6.5). No game rule lives here: a seek is a
// verified Marrow restore of the recorded original, a fork is the server's ForkAsync at that exact tick (the final tick is inspect-only,
// which the shared scrubber already blocks as end_of_timeline and the server refuses with ghostlap_fork_not_allowed).
import type { SeekOptions, TimelineAdapter, VerifiedPreview } from "@sands/timeline-ui";
import { formatTick } from "../ghostLapTimelineAdapter.ts";
import type { GreyboxWire } from "./greyboxWire.ts";
import type { OriginalDescriptor, WorldView } from "./types.ts";

export function createOriginalAdapter(descriptor: OriginalDescriptor, wire: GreyboxWire): TimelineAdapter<WorldView> {
  return {
    sourceId: descriptor.sourceId,
    bounds: descriptor.bounds,
    readOnly: descriptor.readOnly,
    getLabel: (position) => formatTick(position, descriptor.arena.tickRate),
    getMarkers: () => descriptor.markers,
    async seek(position, options?: SeekOptions): Promise<VerifiedPreview<WorldView>> {
      const answer = await wire.restore(descriptor.timelineId, position, options?.signal);
      return { position: answer.position, state: answer.state };
    },
    fork: (position, _options, idempotencyKey) => wire.branch(descriptor.timelineId, position, idempotencyKey),
  };
}
