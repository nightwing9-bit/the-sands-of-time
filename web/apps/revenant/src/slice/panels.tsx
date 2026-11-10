// Presentational panels of the Revenant V1 slice. Each takes server-returned records and renders them; there are no constants
// standing in for results, no fallback numbers, and no derived claim that the server did not back with a field.
import { useMemo } from "react";
import { formatBusinessTime } from "../revenantTimelineAdapter.ts";
import { formatClock, formatMinor, reasonText, statusText, syntheticLabelText } from "./format.ts";
import type { Labelling, RunDescriptor, SliceAligned, SliceComparison, SliceMetrics, SlicePolicy, SliceState } from "./types.ts";

export function TruthBanner({ labelling, policyId }: { labelling: Labelling; policyId: string }) {
  return (
    <aside className="truth" data-testid="truth-banner" data-synthetic={String(labelling.synthetic)} data-projection-only={String(labelling.projectionOnly)} data-ledger-posting={labelling.ledgerPosting}>
      <span className="chip">{labelling.synthetic ? "SYNTHETIC DATA" : "DATA SOURCE NOT LABELLED SYNTHETIC"}</span>
      <span className="chip">{labelling.projectionOnly ? "PROJECTION ONLY" : "PROJECTION FLAG MISSING"}</span>
      <span className="chip" data-testid="ledger-posting">{labelling.ledgerPosting === "none" ? "NO LEDGER POSTING" : `LEDGER POSTING: ${labelling.ledgerPosting}`}</span>
      <span className="chip">SOURCE POLICY {policyId}</span>
      <p>{labelling.note}</p>
    </aside>
  );
}

export function BanksPanel({ run, state }: { run: RunDescriptor; state: SliceState | null }) {
  // Directed activity is a plain tally of the server's events up to the verified position, grouped by source -> destination bank.
  const flows = useMemo(() => {
    if (!state) return [];
    const reached = new Map(state.payments.map((p) => [p.position, p]));
    const groups = new Map<string, { from: string; to: string; count: number; settled: number; held: number; amountMinor: number }>();
    for (const event of run.events) {
      const payment = reached.get(event.position);
      if (!payment) continue;
      const key = `${event.sourceBankId}>${event.destBankId}`;
      const g = groups.get(key) ?? { from: event.sourceBankId, to: event.destBankId, count: 0, settled: 0, held: 0, amountMinor: 0 };
      g.count += 1;
      g.amountMinor += event.amountMinor;
      if (payment.status === "Settled") g.settled += 1;
      if (payment.status === "Held") g.held += 1;
      groups.set(key, g);
    }
    return [...groups.values()].sort((a, b) => a.from.localeCompare(b.from) || a.to.localeCompare(b.to));
  }, [run.events, state]);
  if (!state) return <p data-testid="banks-empty">No position verified by the server yet.</p>;
  return (
    <section aria-label="Six banks" data-testid="banks-panel" data-state-hash={state.stateHash}>
      <h2>Six banks <span className="sub">at {formatBusinessTime(state.businessTime) ?? state.businessTime}</span></h2>
      <table className="num" data-testid="banks-table">
        <thead>
          <tr><th>Bank</th><th>Posted (projection)</th><th>Held reserve</th><th>Available</th></tr>
        </thead>
        <tbody>
          {state.banks.map((bank) => (
            <tr key={bank.bankId} data-testid={`bank-${bank.bankId}`}>
              <th scope="row">{bank.name} <span className="mono">{bank.bankId}</span></th>
              <td data-field="posted" data-minor={bank.postedMinor}>{formatMinor(bank.postedMinor, state.currency)}</td>
              <td data-field="reserved" data-minor={bank.reservedMinor}>{formatMinor(bank.reservedMinor, state.currency)}</td>
              <td data-field="available" data-minor={bank.availableMinor}>{formatMinor(bank.availableMinor, state.currency)}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr data-testid="bank-totals">
            <th scope="row">All six banks</th>
            <td data-field="posted" data-minor={state.totalPostedMinor}>{formatMinor(state.totalPostedMinor, state.currency)}</td>
            <td data-field="reserved" data-minor={state.totalReservedMinor}>{formatMinor(state.totalReservedMinor, state.currency)}</td>
            <td />
          </tr>
        </tfoot>
      </table>
      <h3>Directed activity so far</h3>
      {flows.length === 0 ? (
        <p data-testid="flows-empty">No payment has been applied at this position.</p>
      ) : (
        <table className="num" data-testid="flows-table">
          <thead><tr><th>From → to</th><th>Payments</th><th>Settled</th><th>Held · pending</th><th>Amount initiated</th></tr></thead>
          <tbody>
            {flows.map((f) => (
              <tr key={`${f.from}>${f.to}`} data-testid={`flow-${f.from}-${f.to}`}>
                <th scope="row"><span className="mono">{f.from}</span> → <span className="mono">{f.to}</span></th>
                <td>{f.count}</td><td>{f.settled}</td><td>{f.held}</td><td>{formatMinor(f.amountMinor, state.currency)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

export function EventsPanel({ run, state, position }: { run: RunDescriptor; state: SliceState | null; position: string }) {
  const byPosition = useMemo(() => new Map((state?.payments ?? []).map((p) => [p.position, p])), [state]);
  return (
    <section aria-label="Source payment events" data-testid="events-panel">
      <h2>Source payment events <span className="sub">{run.events.length} exogenous intents, recorded under {run.sourcePolicyId}</span></h2>
      <div className="scroll">
        <table className="num" data-testid="events-table">
          <thead><tr><th>#</th><th>UTC</th><th>From → to</th><th>Amount</th><th>Scenario label</th><th>Status at position</th></tr></thead>
          <tbody>
            {run.events.map((event) => {
              const payment = byPosition.get(event.position);
              return (
                <tr key={event.position} data-testid={`event-${event.position}`} data-status={payment?.status ?? "NotReached"} aria-current={event.position === position ? "true" : undefined}>
                  <td>{event.position}</td>
                  <td>{formatClock(event.businessTime)}</td>
                  <td><span className="mono">{event.sourceAccountId}</span> → <span className="mono">{event.destAccountId}</span></td>
                  <td>{formatMinor(event.amountMinor, event.currency)}</td>
                  <td>{syntheticLabelText(event.syntheticLabel)}</td>
                  <td>{payment ? `${statusText(payment.status)}${payment.reason !== "None" ? ` (${reasonText(payment.reason)})` : ""}` : "not reached yet"}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function ruleText(policy: SlicePolicy): string {
  const parts: string[] = [];
  const windowMinutes = policy.velocityWindowMs / 60_000;
  const floor = policy.velocityAmountFloorMinor > 0 ? ` and amount ≥ ${policy.velocityAmountFloorMinor / 100}` : "";
  parts.push(`${policy.velocityHoldThreshold}+ payments by one payer within ${windowMinutes} min${floor}`);
  if (policy.newBeneficiaryAmountFloorMinor !== null) parts.push(`new beneficiary and amount ≥ ${policy.newBeneficiaryAmountFloorMinor / 100}`);
  if (policy.holdOnSanctionsHit) parts.push("sanctions hit");
  return `Hold when: ${parts.join("; or ")}.`;
}

export function PolicySelector({ policies, value, onChange, disabled }: { policies: SlicePolicy[]; value: string; onChange: (id: string) => void; disabled: boolean }) {
  return (
    <fieldset className="policy" data-testid="policy-selector" disabled={disabled}>
      <legend>Policy for the branch</legend>
      {policies.map((policy) => (
        <label key={policy.policyId} data-testid={`policy-option-${policy.policyId}`}>
          <input type="radio" name="branch-policy" value={policy.policyId} checked={value === policy.policyId} onChange={() => onChange(policy.policyId)} />
          <strong>{policy.policyId}</strong> <span className="sub">{ruleText(policy)}</span>
        </label>
      ))}
    </fieldset>
  );
}

function Trajectory({ aligned, currency }: { aligned: SliceAligned[]; currency: string }) {
  const W = 480;
  const H = 140;
  const pad = 36;
  const series = aligned.map((a) => ({ position: Number(a.position), original: a.original.totalReservedMinor, alternate: a.alternate.totalReservedMinor }));
  const first = series[0];
  const last = series[series.length - 1];
  if (!first || !last) return null;
  const xMin = first.position;
  const xMax = last.position;
  const yMax = Math.max(1, ...series.flatMap((s) => [s.original, s.alternate]));
  const x = (p: number) => pad + (xMax === xMin ? 0 : ((p - xMin) / (xMax - xMin)) * (W - pad - 8));
  const y = (v: number) => H - 20 - (v / yMax) * (H - 36);
  const line = (pick: (s: (typeof series)[number]) => number) => series.map((s) => `${x(s.position).toFixed(1)},${y(pick(s)).toFixed(1)}`).join(" ");
  return (
    <figure data-testid="trajectory">
      <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Held reserve across all six banks by event position, original and alternate">
        <line className="axis" x1={pad} y1={H - 20} x2={W - 8} y2={H - 20} />
        <line className="axis" x1={pad} y1={8} x2={pad} y2={H - 20} />
        <polyline className="line original" points={line((s) => s.original)} fill="none" />
        <polyline className="line alternate" points={line((s) => s.alternate)} fill="none" />
        <text className="tick" x={pad} y={H - 6}>Event {xMin}</text>
        <text className="tick" x={W - 8} y={H - 6} textAnchor="end">Event {xMax}</text>
        <text className="tick" x={pad - 4} y={14} textAnchor="end">{formatMinor(yMax, currency)}</text>
      </svg>
      <figcaption>Total held reserve (all banks) at {series.length} verified positions. Solid = original, dashed = alternate.</figcaption>
      <table className="num visually-compact" data-testid="trajectory-table">
        <thead><tr><th>Event</th><th>Original held reserve</th><th>Alternate held reserve</th></tr></thead>
        <tbody>
          {aligned.map((a) => (
            <tr key={a.position} data-position={a.position}>
              <td>{a.position}</td>
              <td data-minor={a.original.totalReservedMinor}>{formatMinor(a.original.totalReservedMinor, currency)}</td>
              <td data-minor={a.alternate.totalReservedMinor}>{formatMinor(a.alternate.totalReservedMinor, currency)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}

function MetricCards({ metrics, currency }: { metrics: SliceMetrics; currency: string }) {
  return (
    <div className="cards" data-testid="metrics" data-cost-model={metrics.costModel} data-label-schema={metrics.labelSchema}>
      <div className="card" data-testid="metric-fraud-prevented">
        <span className="eyebrow">Synthetic fraud terminally stopped (prevented)</span>
        <b data-value={metrics.fraudPreventedCount}>{metrics.fraudPreventedCount}</b>
        <span className="sub">exposure {formatMinor(metrics.fraudPreventedExposureMinor, currency)}</span>
      </div>
      <div className="card" data-testid="metric-fraud-deferred">
        <span className="eyebrow">Synthetic fraud held, pending at horizon (deferred, not prevented)</span>
        <b data-value={metrics.fraudDeferredAtHorizonCount}>{metrics.fraudDeferredAtHorizonCount}</b>
        <span className="sub">principal is not counted as avoided</span>
      </div>
      <div className="card" data-testid="metric-legit-delayed">
        <span className="eyebrow">Synthetic legitimate payments delayed</span>
        <b data-value={metrics.legitimateDelayedCount}>{metrics.legitimateDelayedCount}</b>
      </div>
      <div className="card" data-testid="metric-false-positive-cost">
        <span className="eyebrow">Assumed false-positive cost</span>
        <b data-value={metrics.falsePositiveCostMinor}>{formatMinor(metrics.falsePositiveCostMinor, currency)}</b>
      </div>
      <div className="card" data-testid="metric-delay-cost">
        <span className="eyebrow">Assumed incremental delay cost</span>
        <b data-value={metrics.incrementalDelayCostMinor}>{formatMinor(metrics.incrementalDelayCostMinor, currency)}</b>
      </div>
      <div className="card" data-testid="metric-net">
        <span className="eyebrow">Net synthetic effect (prevented − assumed costs)</span>
        <b data-value={metrics.netEffectMinor}>{formatMinor(metrics.netEffectMinor, currency)}</b>
      </div>
    </div>
  );
}

export function ComparisonPanel({ comparison }: { comparison: SliceComparison }) {
  const first = comparison.aligned[0];
  const last = comparison.aligned[comparison.aligned.length - 1];
  if (!first || !last) return null;
  const currency = last.original.currency;
  const sharedPrefix = first.original.stateHash === first.alternate.stateHash;
  return (
    <section aria-label="Branch comparison" data-testid="comparison-panel" data-branch-id={comparison.branchId} data-branch-status={comparison.branchStatus}>
      <h2>Branch comparison <span className="sub">same synthetic inputs, different policy</span></h2>
      <p data-testid="comparison-headline">
        <span className="orig">ORIGINAL · {comparison.sourcePolicyId}</span> vs <span className="alt">ALTERNATE · {comparison.branchPolicyId ?? "policy not shown"}</span>, forked after
        event {comparison.forkPosition} of {comparison.finalPosition}. Branch <span className="mono">{comparison.branchId}</span> is {comparison.branchStatus}.
      </p>
      <p className="seal" data-testid="source-check" data-unchanged={String(comparison.sourceCheck.unchanged)}>
        {comparison.sourceCheck.unchanged
          ? "ORIGINAL HISTORY UNCHANGED: replayed final state hash equals the hash recorded before the branch."
          : "ORIGINAL HISTORY CHECK FAILED: replayed final state hash differs from the hash recorded before the branch."}{" "}
        <span className="mono">{comparison.sourceCheck.hashNow.slice(0, 16)}…</span>
      </p>
      <p data-testid="prefix-check" data-shared={String(sharedPrefix)}>
        At the fork (event {first.position}) both histories {sharedPrefix ? "share the same verified state" : "DIFFER, which should not happen"}.
      </p>
      <table className="num" data-testid="comparison-banks">
        <thead>
          <tr><th rowSpan={2}>Bank</th><th colSpan={2} className="orig">Original · available</th><th colSpan={2} className="alt">Alternate · available</th></tr>
          <tr><th>At event {first.position}</th><th>At event {last.position}</th><th>At event {first.position}</th><th>At event {last.position}</th></tr>
        </thead>
        <tbody>
          {last.original.banks.map((bank) => {
            const at = (state: SliceState) => state.banks.find((b) => b.bankId === bank.bankId);
            const cells = [at(first.original), at(last.original), at(first.alternate), at(last.alternate)];
            return (
              <tr key={bank.bankId} data-testid={`cmp-bank-${bank.bankId}`}>
                <th scope="row">{bank.name}</th>
                {cells.map((c, i) => (
                  <td key={i} data-minor={c?.availableMinor ?? ""} data-reserved={c?.reservedMinor ?? ""}>{c ? formatMinor(c.availableMinor, currency) : "n/a"}</td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
      <Trajectory aligned={comparison.aligned} currency={currency} />
      <h3>Decisions that differ <span className="sub" data-testid="difference-count" data-count={comparison.differences.length}>{comparison.differences.length} of {last.original.payments.length}</span></h3>
      {comparison.differences.length === 0 ? (
        <p data-testid="differences-empty">Both policies made the same decision for every payment.</p>
      ) : (
        <div className="scroll">
          <table className="num" data-testid="differences-table">
            <thead><tr><th>#</th><th>Amount</th><th>Scenario label</th><th className="orig">Original</th><th className="alt">Alternate</th></tr></thead>
            <tbody>
              {comparison.differences.map((d) => (
                <tr key={d.position} data-testid={`difference-${d.position}`} data-label={d.syntheticLabel} data-alt-status={d.alternate.status}>
                  <td>{d.position}</td>
                  <td>{formatMinor(d.amountMinor, d.currency)}</td>
                  <td>{syntheticLabelText(d.syntheticLabel)}</td>
                  <td>{statusText(d.original.status)} ({d.original.policyId})</td>
                  <td>{statusText(d.alternate.status)} ({reasonText(d.alternate.reason)}, {d.alternate.policyId})</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <h3>Synthetic scenario metrics <span className="sub">at event {last.position}, cost model {last.metrics.costModel}</span></h3>
      <MetricCards metrics={last.metrics} currency={currency} />
      <p className="caveat" data-testid="metrics-caveat">
        These counts use synthetic scenario labels, not real-world fraud findings. A held payment is pending, not prevented. Costs are scenario assumptions, not real banking losses.
      </p>
    </section>
  );
}
