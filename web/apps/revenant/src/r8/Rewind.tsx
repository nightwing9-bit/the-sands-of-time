import { useMemo, useState } from "react";
import { isTimelinePosition, type TimelinePosition } from "@sands/contracts";
import { TimelineScrubber, type TimelineAdapter } from "@sands/timeline-ui";
import { bufferReference } from "./bufferReference.ts";
import { BufferNote, placementOf, TimeChart, type LaneMarker, type Series } from "./charts.tsx";
import { BANKS, bankAlias, clock, money, shortHash, signedInteger, time } from "./format.ts";
import { BankSelect } from "./Liquidity.tsx";
import { Drawer, ErrorText, Hourglass, Icon, Table, unwrap, useLoad, useOperation, type Api, type Balance, type Job, type Preview, type S, type Snapshot, type Verified } from "./shared.tsx";

type Field = "availableMinor" | "postedMinor" | "reservedMinor";
const fieldName: Record<Field, string> = { availableMinor: "Available", postedMinor: "Posted", reservedMinor: "Reserved" };

export function Rewind({ api, state, job, jobError, reloadJob, chooseJob, reload, forkPosition }: { api: Api; state: Snapshot; job: Job | null; jobError: string;
  reloadJob: () => Promise<void>; chooseJob: (job: string, fork: string) => void; reload: () => Promise<void>; forkPosition: string | null }) {
  const op = useOperation();
  const [position, setPosition] = useState<TimelinePosition>("0");
  const [preview, setPreview] = useState<Preview | null>(null);
  const [policy, setPolicy] = useState("policy-v8"), [mode, setMode] = useState("policy"), [intent, setIntent] = useState(""),
    [action, setAction] = useState("Reject"), [version, setVersion] = useState(1);
  const sourceVerified = state.status === "Verified";
  const liquidity = useLoad(useMemo(() => () => unwrap(api.get("/revenant/r8/runs/{id}/liquidity", { path: { id: state.runId } })), [api, state.runId]));
  const audit = useLoad(useMemo(() => sourceVerified ? () => unwrap(api.get("/revenant/r8/runs/{id}/audit", { path: { id: state.runId } })) : null, [api, state.runId, sourceVerified]));
  const seals = useMemo(() => state.markers.filter(x => x.kind === "ObservationSeal"), [state.markers]);
  const eligible = seals.find(x => x.position === position && x.atMs < state.boundaryMs);
  const pending = job?.status === "Pending" || job?.status === "Running";
  const verified = job?.status === "Verified" && job.verified ? job.verified : null;
  const sourcePolicy = verified?.sourcePolicy ?? audit.value?.policy ?? state.payments.find(p => p.risk)?.risk?.policy ?? "source policy";
  const dockMarkers = useMemo(() => seals.filter((x, i) => i >= seals.length - 4 || x.atMs === state.payments[0]?.acceptedAtMs), [seals, state.payments]);
  const adapter = useMemo<TimelineAdapter<Preview>>(() => ({
    sourceId: state.timelineId, bounds: { start: "0", end: state.lastPosition }, readOnly: true,
    getLabel: p => { const mark = state.markers.find(x => x.position === p); return mark ? `Event ${p} · ${time(mark.atMs)} · ${mark.kind}` : `Event ${p}`; },
    getMarkers: () => dockMarkers.map(m => ({ position: m.position, label: time(m.atMs) + " completed seal" })),
    seek: async (p, o) => {
      if (!isTimelinePosition(p)) throw new Error("Invalid event position.");
      const value = await unwrap(api.get("/revenant/r8/runs/{id}/timeline/{position}", { path: { id: state.runId, position: p }, signal: o?.signal }));
      return { position: value.position, state: value };
    },
    fork: async () => { throw new Error("Submit a sandbox replay from a completed seal."); },
  }), [api, state.timelineId, state.runId, state.lastPosition, state.markers, dockMarkers]);
  const payload: S["R8Replay"] = { forkPosition: position, horizonMs: state.boundaryMs, policy,
    interventions: mode === "policy" ? null : eligible ? [{ intentId: intent, action, expectedVersion: version,
      atMs: Math.max(eligible.atMs, state.payments.find(p => p.intentId === intent)?.acceptedAtMs ?? eligible.atMs) }] : null };
  const interventionInvalid = mode !== "policy" && (!intent || !Number.isInteger(version) || version < 1);
  const canRun = Boolean(eligible && preview && !pending && !op.busy && !interventionInvalid);
  const blockReason = !sourceVerified ? "Close and verify the source before replaying it."
    : pending ? "A replay is in progress." : !preview ? "Select a completed seal on the Timeline below."
    : !eligible ? "This event is not a completed observation seal before the horizon; choose a seal marker."
    : interventionInvalid ? "Choose the payment and a case version for the sandbox intervention." : null;
  const phase = verified ? "result" : pending ? "running" : eligible && preview ? "fork" : "original";
  const forkMarker = (verified || job) && forkPosition ? state.markers.find(m => m.position === forkPosition) ?? null : eligible ?? null;

  const auditOk = audit.value && audit.value.status === "Verified" && audit.value.ledgerVerification === "Verified";
  const unchanged = Boolean(auditOk && verified && audit.value!.recordHash === verified.sourceRecordHash && audit.value!.stateHash === verified.sourceStateHash);
  const heading = !sourceVerified ? "Close the live session to make its history a verified original."
    : phase === "result" ? `${forkMarker ? `At ${clock(forkMarker.atMs)}, ` : ""}${verified!.alternativePolicy} changes the replay and exposes the trade-offs.`
    : phase === "running" ? "The sandbox is replaying the original from the fork."
    : phase === "fork" ? `A decision point at ${clock(eligible!.atMs)}. A different rule is ready.`
    : "Explore the completed original history, without changing a single entry.";

  return <div className={"r8-rewind is-" + phase}>
    <div className="r8-scenario-bar">
      <span className="r8-chip is-tonal">{sourceVerified ? "VERIFIED SOURCE" : "LIVE SOURCE"} · RUN {state.runId.slice(0, 8).toUpperCase()}</span>
      <div className="r8-scenario-meta"><span className="r8-eyebrow">SOURCE · {sourceVerified ? "CLOSED LEDGER" : "OPEN LEDGER"}</span>
        <span>Events 0–{state.lastPosition} · horizon {clock(state.boundaryMs)} UTC · {sourcePolicy}</span></div>
      {(phase === "fork" || phase === "running") && <span className="r8-chip is-policy">{sourcePolicy} <b>→</b> {verified?.alternativePolicy ?? policy}</span>}
      {verified && <span className="r8-chip is-coral" title={verified.childTimelineId}>{verified.sourcePolicy} → CHILD {verified.childTimelineId.slice(0, 8)} · {verified.alternativePolicy}</span>}
      <div className="r8-scenario-actions">
        {sourceVerified ? <>
          <label className="r8-inline-label">Alternative policy <select value={policy} onChange={e => setPolicy(e.target.value)}><option value="policy-v8">policy-v8 · V8 rules + pinned R5</option></select></label>
          <label className="r8-inline-label"><span className="r8-sr">Comparison mode</span><select aria-label="Comparison mode" value={mode} onChange={e => setMode(e.target.value)}>
            <option value="policy">Policy only</option><option value="intervention">Policy + sandbox intervention</option></select></label>
          <button className={"r8-btn " + (verified ? "" : "is-primary")} disabled={!canRun} title={blockReason ?? undefined}
            onClick={() => void op.run(JSON.stringify(payload), async key => {
              const j = await unwrap(api.post("/revenant/r8/runs/{id}/counterfactual", { path: { id: state.runId }, body: payload, idempotencyKey: key }));
              chooseJob(j.jobId, payload.forkPosition);
            })}>Run sandbox replay<Icon name="arrow" /></button>
        </> : <button className="r8-btn is-primary" disabled={op.busy} onClick={() => void op.run("close", async key => {
          await unwrap(api.post("/revenant/r8/runs/{id}/close", { path: { id: state.runId }, idempotencyKey: key })); await reload(); return "Source verified";
        })}>{op.busy ? "Verifying source…" : "Close and verify source"}</button>}
        <a className="r8-btn" href="#tower">Exit Rewind</a>
      </div>
    </div>
    {sourceVerified && mode === "intervention" && <div className="r8-intervention" role="group" aria-label="Sandbox intervention">
      <span className="r8-eyebrow">SANDBOX INTERVENTION · REPLAY INPUT ONLY, NEVER A LIVE DECISION · FRESH MFA REQUIRED</span>
      <label className="r8-inline-label">Payment <select value={intent} onChange={e => setIntent(e.target.value)}><option value="">Choose payment</option>{state.payments.map(p => <option key={p.intentId}>{p.intentId}</option>)}</select></label>
      <label className="r8-inline-label">Analyst action <select value={action} onChange={e => setAction(e.target.value)}><option>Reject</option><option>Approve</option></select></label>
      <label className="r8-inline-label">Expected case version <input type="number" min="1" value={version} onChange={e => setVersion(Number(e.target.value))} /></label>
    </div>}

    <div className="r8-heading is-rewind">
      <h1>{heading}</h1>
      <div className="r8-rewind-status">
        {job ? <p role="status" data-testid="r8-job-status" className={"r8-job is-" + job.status.toLowerCase()}>Replay {job.status} {job.failure && `· ${job.failure}`}</p>
          : <p className="r8-heading-note">{blockReason ? blockReason.toUpperCase() : "FORK FIXED · AWAITING REPLAY"}</p>}
        {job && <ol className="r8-stepper" aria-label="Replay job states">{["Pending", "Running", "Verified"].map(s => <li key={s} className={stepClass(job.status, s)}>{s}</li>)}</ol>}
        {pending && job && <button className="r8-btn is-small" disabled={op.busy} onClick={() => void op.run("cancel:" + job.jobId, async key => {
          await unwrap(api.post("/revenant/r8/runs/{id}/counterfactual/{job}/cancel", { path: { id: state.runId, job: job.jobId }, idempotencyKey: key })); await reloadJob();
        })}>Cancel replay</button>}
      </div>
    </div>
    <div className="r8-rewind-errors"><ErrorText error={op.error || jobError} />{op.note && <p className="r8-ok" role="status">{op.note}</p>}
      {job?.status === "Failed" && <p className="r8-error" role="alert">The sandbox replay failed and produced no verified comparison. No KPI or child hash is shown.</p>}
      {job?.status === "Cancelled" && <p className="r8-error" role="alert">The sandbox replay was cancelled. No comparison was produced.</p>}</div>

    {verified ? <Comparison value={verified} opening={state.opening} forkAt={forkMarker?.atMs ?? null} sourcePolicy={sourcePolicy} />
      : <SourceHistory state={state} points={liquidity.value?.actual ?? []} error={liquidity.error} loading={!liquidity.value && !liquidity.error}
        fork={eligible ?? null} policy={sourcePolicy} />}

    <div className="r8-dock">
      <div className="r8-dock-glass"><Hourglass className="r8-dock-hourglass" level={fractionOf(position, state.lastPosition)} /></div>
      <div className="r8-dock-main">
        <div className="r8-dock-ticks" aria-hidden="true">{dockMarkers.map(m => <span key={m.position} style={{ left: fractionOf(m.position, state.lastPosition) * 100 + "%" }}
          className={m.position === position ? "is-active" : ""}>{clock(m.atMs).slice(0, 5)}</span>)}</div>
        <TimelineScrubber adapter={adapter} position={position} onVerified={v => { setPosition(v.position); setPreview(v.state); }} />
      </div>
      <div className="r8-dock-side">
        <div className="r8-dock-time">{preview ? clock(preview.boundaryMs) : "--:--:--"}</div>
        {preview ? <p className="r8-dock-preview" data-testid="r8-preview">Verified event {preview.position} · {time(preview.boundaryMs)} · <code>{preview.stateHash.slice(0, 16)}…</code></p>
          : <p className="r8-dock-preview">Seek or pick a seal marker</p>}
        <span className={"r8-dock-fork" + (forkMarker ? " is-set" : "")} title={forkMarker ? `Event ${forkMarker.position}` : undefined}><Icon name="fork" />{forkMarker ? `Fork ${clock(forkMarker.atMs)}` : "No fork set"}</span>
        <div className={"r8-seal" + (auditOk ? " is-ok" : "")}>
          <strong>{!sourceVerified ? "LIVE SOURCE · NOT YET CLOSED" : audit.error ? "SOURCE VERIFICATION UNAVAILABLE" : !audit.value ? "VERIFYING SOURCE…"
            : unchanged ? "✓ ORIGINAL ARCHIVE UNCHANGED" : auditOk ? "✓ SOURCE CHAIN VERIFIED" : "SOURCE VERIFICATION FAILED"}</strong>
          <span>{audit.value ? `record ${shortHash(audit.value.recordHash)} · read only` : "read only · source never overwritten"}</span>
        </div>
      </div>
    </div>
  </div>;
}

function stepClass(status: string, step: string) {
  const order = ["Pending", "Running", "Verified"];
  if (status === "Failed" || status === "Cancelled") return step === "Verified" ? "is-failed" : "is-done";
  const i = order.indexOf(status), j = order.indexOf(step);
  return j < i ? "is-done" : j === i ? "is-current" : "";
}
function fractionOf(position: string, end: string) {
  const e = BigInt(end), p = /^[0-9]+$/.test(position) ? BigInt(position) : 0n;
  return e <= 0n ? 0 : Number((p * 10000n) / e) / 10000;
}

function SourceHistory({ state, points, error, loading, fork, policy }: { state: Snapshot; points: { atMs: number; balance: Balance }[]; error: string; loading: boolean;
  fork: S["R8Marker"] | null; policy: string }) {
  // Default: the bank whose available balance moved most in the source history (deterministic, ties by bank order).
  const defaultBank = useMemo(() => {
    let best = "BANK-A", spread = -1n;
    for (const b of BANKS) {
      const v = points.filter(x => x.balance.bankId === b.id).map(x => BigInt(x.balance.availableMinor));
      if (v.length === 0) continue;
      const d = v.reduce((a, c) => (c > a ? c : a)) - v.reduce((a, c) => (c < a ? c : a));
      if (d > spread) { spread = d; best = b.id; }
    }
    return best;
  }, [points]);
  const [chosen, setChosen] = useState<string | null>(null);
  const bank = chosen ?? defaultBank;
  const values = points.filter(x => x.balance.bankId === bank).map(x => ({ at: x.atMs, minor: x.balance.availableMinor }));
  const markers: LaneMarker[] = state.markers.map(m => ({ at: m.atMs, kind: m.kind, label: `Event ${m.position} · ${m.kind}${m.intentId ? " · " + m.intentId : ""} · ${time(m.atMs)}` }));
  return <section className="r8-card r8-rewind-chart" aria-label="Source history">
    <div className="r8-card-head"><h2 className="r8-eyebrow">{bankAlias(bank).name} available balance · {fork ? "decision point" : "original history"} <small>{bank}</small></h2>
      <div className="r8-chips"><BankSelect value={bank} onChange={setChosen} /><span className="r8-chip">ORIGINAL · {policy}</span></div></div>
    <div className="r8-rewind-plot">
      {error ? <p className="r8-error" role="alert">{error}</p> : loading ? <p className="r8-loading" role="status">Loading source observations…</p> :
        <TimeChart label={`${bank} source history available balance`} yLabel="USD · committed available balance" markers={markers}
          fork={fork ? { at: fork.atMs, label: `FORK ${clock(fork.atMs)}` } : null}
          callouts={values.length ? [{ series: "Original", label: `ORIGINAL · ${policy}` }] : []}
          series={[{ name: "Original", tone: "source", area: true, values }]} />}
    </div>
    <div className="r8-chart-foot"><p className="r8-muted">Per-bank committed observations at completed seals · {state.status === "Verified" ? "completed source, read-only" : "live source, still open"} · lane: ▲ screening · ● other facts · | seals</p>
      <Drawer summary="View as table" className="is-up"><Table caption={`${bank} source available balance`} headers={["Seal time", "Available"]} rows={values.map(v => [time(v.at), money(v.minor)])} /></Drawer></div>
  </section>;
}

function Comparison({ value, opening, forkAt, sourcePolicy }: { value: Verified; opening: Balance[]; forkAt: number | null; sourcePolicy: string }) {
  const differing = BANKS.find(b => value.balances.some(x => x.bankId === b.id && x.source.availableMinor !== x.alternative.availableMinor))?.id;
  const [bank, setBank] = useState(differing ?? value.balances[0]?.bankId ?? "BANK-A");
  const [field, setField] = useState<Field>("availableMinor");
  const b = value.balances.filter(x => x.bankId === bank);
  const reference = field === "availableMinor" ? bufferReference(opening, bank) : undefined;
  const firstDiff = b.find(x => x.source[field] !== x.alternative[field])?.atMs ?? null;
  const wedgeFrom = forkAt ?? firstDiff;
  const series: Series[] = [
    { name: "Source history", tone: "source", area: true, values: b.map(x => ({ at: x.atMs, minor: x.source[field] })) },
    { name: "Verified sandbox", tone: "alternative", values: b.map(x => ({ at: x.atMs, minor: x.alternative[field] })) },
  ];
  const metric = (x: S["R8Headline"], key: "source" | "alternative" | "delta") => x.unit === "USD minor units" ? money(x[key], key === "delta") : key === "delta" ? signedInteger(x[key]) : x[key];
  const heroIndex = Math.max(0, value.headlines.findIndex(h => /net/i.test(h.label)));
  return <div className="r8-compare">
    <section className="r8-card r8-rewind-chart" aria-label="Verified financial comparison">
      <div className="r8-card-head"><h2 className="r8-eyebrow">{bankAlias(bank).name} {fieldName[field].toLowerCase()} · source vs executed child <small>{bank}</small></h2>
        <div className="r8-chips"><BankSelect value={bank} onChange={setBank} />
          <label className="r8-select"><span className="r8-sr">Balance</span><select aria-label="Balance" value={field} onChange={e => setField(e.target.value as Field)}>
            <option value="availableMinor">Available</option><option value="postedMinor">Posted</option><option value="reservedMinor">Reserved</option></select></label>
          <span className="r8-chip">ORIGINAL · {value.sourcePolicy}</span><span className="r8-chip is-coral">CHILD · {value.alternativePolicy}</span></div></div>
      <div className="r8-rewind-plot">
        <TimeChart reference={reference} showNote={false} label={bank + " source and completed alternative balances"} yLabel={`USD · committed ${fieldName[field].toLowerCase()} balance`}
          fork={forkAt !== null ? { at: forkAt, label: `FORK ${clock(forkAt)}` } : null}
          wedge={wedgeFrom !== null ? { a: "Verified sandbox", b: "Source history", from: wedgeFrom } : null}
          callouts={b.length ? [{ series: "Verified sandbox", label: `ALT · ${value.alternativePolicy}` }, { series: "Source history", label: `ORIGINAL · ${value.sourcePolicy}` }] : []}
          series={series} />
      </div>
      <div className="r8-chart-foot">
        <p className="r8-muted">Source history and executed sandbox child · {value.mode === "PolicyOnly" ? "policy only" : "policy + sandbox intervention"} · {forkAt === null ? (firstDiff !== null ? `shading from first observed difference ${clock(firstDiff)}` : "no observed difference for this bank") : "compared segment from the fork"} · curve endpoint difference ≠ net economic effect</p>
        <div className="r8-drawers">
          <Drawer summary="Aligned balance data" className="is-up">
            {reference && <p>Shared operating buffer reference: {reference.status === "Available" ? money(reference.minor) : "Unavailable"}</p>}
            {reference && <BufferNote reference={reference} placement={placementOf(series, reference)} />}
            <Table caption={`${bank} aligned balances`} headers={["Time", "Source", "Alternative"]} rows={b.map(x => [time(x.atMs), money(x.source[field]), money(x.alternative[field])])} />
          </Drawer>
          <Drawer summary="Outcome breakdown" className="is-up">
            <Table headers={["Outcome", "Source", "Alternative", "Delta"]} rows={value.breakdown.map(x => [x.label, metric(x, "source"), metric(x, "alternative"), metric(x, "delta")])} />
            <p className="r8-muted">{value.costPolicy} · {value.continuationPolicy}</p>
          </Drawer>
          <Drawer summary="Inspect underlying payments and evidence" className="is-up">
            <Table headers={["Intent", "Principal", "Source", "Alternative", "Cause", "Delay", "Evidence"]} rows={value.payments.map(p => [
              p.intentId, money(p.principalMinor), p.sourceStatus, p.alternativeStatus, p.alternativeCause, p.delayMinutes + " min",
              <span className="r8-mono" title={`${p.sourceEvidence ?? "Unavailable"} / ${p.alternativeEvidence ?? "Unavailable"}`}>{shortHash(p.sourceEvidence)} / {shortHash(p.alternativeEvidence)}</span>])} />
            {(value.actions ?? []).length > 0 && <Table headers={["Intent", "Reviewer", "Time", "Action", "Disposition"]} rows={(value.actions ?? []).map(a => [a.intentId, a.actor, time(a.atMs), a.action, a.status + " · " + a.code])} />}
          </Drawer>
        </div>
      </div>
    </section>
    <section className="r8-kpi-col" aria-label="Verified economic outcome">
      {value.headlines.map((x, i) => <div key={x.label} data-testid="r8-kpi" data-delta={x.delta} className={"r8-quad" + (i === heroIndex ? " is-hero" : "") + " is-k" + i}
        style={{ order: i === heroIndex ? -1 : i }}>
        <div className="r8-eyebrow">{x.label}</div>
        <p className="r8-figure">{metric(x, "delta")}<small>{x.unit === "USD minor units" ? "USD" : x.unit}</small></p>
        <span className="r8-quad-delta">{metric(x, "source")} → {metric(x, "alternative")}</span>
        <span className="r8-quad-context">{kpiContext(x.label, value)}</span>
      </div>)}
      <p className="r8-integrity-line">Verified branch <code title={value.childTimelineId}>{value.childTimelineId}</code> · record {shortHash(value.childRecordHash)} · source {sourcePolicy} unchanged by construction</p>
    </section>
  </div>;
}

function kpiContext(label: string, value: Verified) {
  if (/fraud/i.test(label)) return "Terminal fraud intercepted, scored against synthetic truth labels inside this verified comparison";
  if (/delay/i.test(label)) return "Legitimate payments settled later than in the source";
  if (/false|cost/i.test(label)) return "Cost of holding legitimate transfers, per the pinned cost policy";
  if (/net/i.test(label)) return value.mode === "PolicyOnly" ? "Policy effect only · alternative − source"
    : `Policy + ${(value.actions ?? []).length} sandbox analyst action${(value.actions ?? []).length === 1 ? "" : "s"} · alternative − source`;
  return "Alternative − source";
}
