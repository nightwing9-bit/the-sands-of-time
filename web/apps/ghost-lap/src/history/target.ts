// What the replay screen is asked to open, and how that survives a refresh (Spec 13, task 13.10). Pure: no React, no network.
// A target is only ever an id the owner routes already returned. The replay URL carries `replay=original|child` and the id, nothing else, so a
// refresh re-reads the same authenticated resource: an Original through /originals/{id}/trace, a saved future through /children/{id}/trace.
import type { SoloChildWire, SoloOriginalWire } from "../solo/soloWire.ts";
import { versionKey, type ChildKind, type ChildSummaryWire, type TraceKind } from "./historyWire.ts";

export type ReplayTarget =
  | { readonly kind: "original"; readonly id: string; readonly original: SoloOriginalWire | null }
  | { readonly kind: "child"; readonly id: string; readonly child: SoloChildWire | null; readonly source: SoloOriginalWire | null };

/** Everything the screen needs to open and word one history. Every field comes from a server answer, never from the browser. */
export interface ReplayMeta {
  readonly kind: TraceKind;
  readonly id: string;
  readonly mascotId: number;
  /** From the library row, when the user came from it. Pinned against the first chunk; absent after a refresh (the chunk then defines it). */
  readonly finalStateHash: string | undefined;
  readonly versionKey: string | undefined;
  readonly sourceId: string | null;
  readonly childKind: ChildKind | null;
  readonly forkTick: number;
  /** The source Original as the library knew it (its hash pins the optional "show the original" trace). */
  readonly sourceKnown: SoloOriginalWire | null;
  /** A server-verified final score if the library already held one. */
  readonly finalScore: number | null;
}

export const originalVersionKey = (o: SoloOriginalWire): string => versionKey(o);

export function metaFromOriginal(o: SoloOriginalWire): ReplayMeta {
  return { kind: "original", id: o.id, mascotId: o.mascotId, finalStateHash: o.finalStateHash, versionKey: originalVersionKey(o), sourceId: null, childKind: null, forkTick: 0, sourceKnown: null, finalScore: o.score };
}
export function metaFromChild(c: SoloChildWire, source: SoloOriginalWire | null): ReplayMeta {
  return { kind: "child", id: c.id, mascotId: c.mascotId, finalStateHash: c.finalStateHash, versionKey: versionKey(c.versions), sourceId: c.sourceId, childKind: c.kind === "Fork" ? "Fork" : "Challenge", forkTick: c.forkTick, sourceKnown: source, finalScore: c.verified ? c.score : null };
}
export function metaFromSummary(s: ChildSummaryWire): ReplayMeta {
  return { kind: "child", id: s.id, mascotId: s.mascotId, finalStateHash: s.finalStateHash, versionKey: undefined, sourceId: s.sourceId, childKind: s.kind, forkTick: s.forkTick, sourceKnown: null, finalScore: s.livingScore };
}
/** The meta a target already carries, or null when only an id is known (a refresh) and the server must be asked first. */
export function metaOfTarget(t: ReplayTarget): ReplayMeta | null {
  if (t.kind === "original") return t.original ? metaFromOriginal(t.original) : null;
  return t.child ? metaFromChild(t.child, t.source) : null;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** `?screen=replay&replay=child&id=…`. Anything that is not a D-form UUID is refused here, before it can reach a route. */
export function parseReplayParams(search: string): { kind: TraceKind; id: string } | null {
  const q = new URLSearchParams(search);
  const kind = q.get("replay"), id = q.get("id");
  return (kind === "original" || kind === "child") && id !== null && UUID.test(id) ? { kind, id } : null;
}
export function targetFromParams(p: { kind: TraceKind; id: string }): ReplayTarget {
  return p.kind === "original" ? { kind: "original", id: p.id, original: null } : { kind: "child", id: p.id, child: null, source: null };
}
