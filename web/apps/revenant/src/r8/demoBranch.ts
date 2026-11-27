import { interpolateAt, type Point } from "./charts.tsx";

/**
 * Demo-only presentation adaptation. The prepared scenario holds one alternate trajectory, forked at 09:00. For any other minute
 * the same prepared divergence is replayed from the new fork: the alternate starts exactly on the original curve at that instant
 * and drifts away by the prepared amounts (measured from the fork, held at the last prepared value). No ledger, no model.
 */
export type BranchRow = { at: number; source: bigint; alternative: bigint };

export function branchAlternate(rows: BranchRow[], preparedFork: number, fork: number) {
  const source: Point[] = rows.map(r => ({ at: r.at, minor: r.source.toString() }));
  const divergence: Point[] = rows.filter(r => r.at >= preparedFork).map(r => ({ at: r.at - preparedFork, minor: (r.alternative - r.source).toString() }));
  const reach = divergence.at(-1)?.at ?? 0;
  const drift = (at: number) => interpolateAt(divergence, Math.min(at - fork, reach)) ?? 0n;
  const aligned = rows.map(r => (r.at <= fork ? r.source : r.source + drift(r.at)));
  const start = interpolateAt(source, fork);
  // Nothing after the final sample: a single point on the original, never a fabricated future.
  const series: Point[] = start === null ? [] : [{ at: fork, minor: start.toString() },
    ...rows.flatMap((r, i) => (r.at > fork ? [{ at: r.at, minor: aligned[i]!.toString() }] : []))];
  return { aligned, series };
}
