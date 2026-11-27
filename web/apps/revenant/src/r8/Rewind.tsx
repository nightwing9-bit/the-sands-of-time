import { useEffect, useMemo, useState, type CSSProperties } from "react";
import { isTimelinePosition, type TimelinePosition } from "@sands/contracts";
import { TimelineScrubber, type TimelineAdapter } from "@sands/timeline-ui";
import { preparedHistory, preparedRewind, preparedScenario } from "./demo/fixtures.ts";
import { atMinute, DEFAULT_FORK_MIN, DemoTimeline, useGraphPlayback } from "./DemoTimeline.tsx";
import { RewindDeck, useRewindSequence } from "./RewindDeck.tsx";
import { RewindStage } from "./RewindStage.tsx";
import { bufferReference } from "./bufferReference.ts";
import { BufferNote, placementOf, TimeChart, type LaneMarker, type Series } from "./charts.tsx";
import { BANKS, bankAlias, clock, demoMoney, money, shortHash, signedInteger, time } from "./format.ts";
import { BankSelect } from "./Liquidity.tsx";
import { Drawer, ErrorText, Hourglass, Icon, Table, unwrap, useLoad, useOperation, useSize, type Api, type Balance, type Job, type Preview, type S, type Snapshot, type Verified } from "./shared.tsx";

const demo = import.meta.env.REVENANT_DEMO === true;
const amount = demo ? demoMoney : money;

type Field = "availableMinor" | "postedMinor" | "reservedMinor";
const fieldName: Record<Field, string> = { availableMinor: "Available", postedMinor: "Posted", reservedMinor: "Reserved" };

export function Rewind({ api, state, job, jobError, reloadJob, chooseJob, reload, forkPosition }: { api: Api; state: Snapshot; job: Job | null; jobError: string;
  reloadJob: () => Promise<void>; chooseJob: (job: string, fork: string) => void; reload: () => Promise<void>; forkPosition: string | null }) {
  const op = useOperation();
  const [position, setPosition] = useState<TimelinePosition>("0");
  const [preview, setPreview] = useState<Preview | null>(null);
  const [policy, setPolicy] = useState("policy-v8"), [mode, setMode] = useState("policy"), [intent, setIntent] = useState(""),
    [action, setAction] = useState("Reject"), [version, setVersion] = useState(1);
  // Demo: the prepared original is open the moment Rewind opens; the sequence below is a scripted visual demonstration.
  const sourceVerified = demo || state.status === "Verified";
  const alternate = policy === "policy-v8";
  const seq = useRewindSequence(alternate);
  // Demo Timeline: `selected` follows the handle (preview), `confirmed` is the fork fixed by "Fork here", `run` is the fork of the displayed run.
  const [selectedMin, setSelectedMin] = useState(DEFAULT_FORK_MIN);
  const [confirmedMin, setConfirmedMin] = useState(DEFAULT_FORK_MIN);
  const [runMin, setRunMin] = useState(DEFAULT_FORK_MIN);
  const [burst, setBurst] = useState(0);
  const playback = useGraphPlayback();
  const liquidity = useLoad(useMemo(() => demo ? null : () => unwrap(api.get("/revenant/r8/runs/{id}/liquidity", { path: { id: state.runId } })), [api, state.runId]));
  const audit = useLoad(useMemo(() => sourceVerified ? () => unwrap(api.get("/revenant/r8/runs/{id}/audit", { path: { id: state.runId } })) : null, [api, state.runId, sourceVerified]));
  const seals = useMemo(() => state.markers.filter(x => x.kind === "ObservationSeal"), [state.markers]);
  const horizon = demo ? preparedRewind.horizon : state.boundaryMs;
  const eligible = seals.find(x => x.position === position && x.atMs < horizon);
  const pending = !demo && (job?.status === "Pending" || job?.status === "Running");
  const demoVerified = useMemo(() => demo ? (preparedScenario.replay("Verified") as { verified: Verified }).verified : null, []);
  const jobVerified = job?.status === "Verified" && job.verified ? job.verified : null;
  const verified = demo ? (alternate && seq.stage === "done" ? demoVerified : null) : jobVerified;
  const sourcePolicy = verified?.sourcePolicy ?? audit.value?.policy ?? state.payments.find(p => p.risk)?.risk?.policy ?? "source policy";
  const dockMarkers = useMemo(() => seals.filter((x, i) => i >= seals.length - 4 || (demo ? x === seals[0] || x.position === preparedRewind.fork.position : x.atMs === state.payments[0]?.acceptedAtMs)), [seals, state.payments]);
  const [ticksRef, { width: trackWidth }] = useSize<HTMLDivElement>();
  const markerOffsets = useMemo(() => dockMarkerOffsets(dockMarkers, state.lastPosition, trackWidth), [dockMarkers, state.lastPosition, trackWidth]);
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
  const payload: S["R8Replay"] = { forkPosition: position, horizonMs: horizon, policy,
    interventions: mode === "policy" ? null : eligible ? [{ intentId: intent, action, expectedVersion: version,
      atMs: Math.max(eligible.atMs, state.payments.find(p => p.intentId === intent)?.acceptedAtMs ?? eligible.atMs) }] : null };
  const interventionInvalid = mode !== "policy" && (!intent || !Number.isInteger(version) || version < 1);
  const canRun = Boolean(eligible && preview && !pending && !op.busy && !interventionInvalid);
  const blockReason = !sourceVerified ? (demo ? "Prepare the original before replaying it." : "Close and verify the source before replaying it.")
    : pending ? "A replay is in progress." : !preview ? "Select a completed seal on the Timeline below."
    : !eligible ? "This event is not a completed observation seal before the horizon; choose a seal marker."
    : interventionInvalid ? "Choose the payment and a case version for the sandbox intervention." : null;
  const sequencing = seq.stage !== "original" && seq.stage !== "done";
  const forkReady = demo && confirmedMin !== runMin;
  const phase = verified ? "result" : pending || sequencing ? "running" : demo ? (forkReady ? "fork" : "original") : eligible && preview ? "fork" : "original";
  const forkMarker = demo ? null
    : (verified || job) && forkPosition ? state.markers.find(m => m.position === forkPosition) ?? null : preview ? eligible ?? null : null;
  const runAt = atMinute(runMin);
  useEffect(() => { if (sequencing) playback.stop(); }, [sequencing, playback.stop]);

  const auditOk = audit.value && audit.value.status === "Verified" && audit.value.ledgerVerification === "Verified";
  const unchanged = Boolean(auditOk && verified && audit.value!.recordHash === verified.sourceRecordHash && audit.value!.stateHash === verified.sourceStateHash);
  const heading = !sourceVerified ? (demo ? "Explore six hours of original policy history." : "Close the live session to make its history a verified original.")
    : phase === "result" ? `${demo ? `At ${clock(runAt)}, ` : forkMarker ? `At ${clock(forkMarker.atMs)}, ` : ""}${verified!.alternativePolicy} ${demo ? "projects a higher network position." : "changes the replay and exposes the trade-offs."}`
    : phase === "running" ? (demo ? "The prepared replay is running from the fork." : "The sandbox is replaying the original from the fork.")
    : phase === "fork" ? `A decision point at ${clock(demo ? atMinute(confirmedMin) : eligible!.atMs)}. A different rule is ready.`
    : "Explore the completed original history, without changing a single entry.";

  const startDemo = () => {
    // The visual run uses the confirmed minute. The prepared job behind it stays the approved 09:00 one, so Audit is untouched.
    const fork = demo ? preparedRewind.fork.position : eligible && preview ? eligible.position : preparedRewind.fork.position;
    playback.stop();
    setRunMin(confirmedMin);
    seq.start();
    // Keeps the Audit screen linked to the prepared comparison, exactly as the previous replay action did. Visual timing does not wait for it.
    if (alternate) void op.run(JSON.stringify({ fork, policy }), async key => {
      const j = await unwrap(api.post("/revenant/r8/runs/{id}/counterfactual", { path: { id: state.runId },
        body: { forkPosition: fork, horizonMs: horizon, policy, interventions: null }, idempotencyKey: key }));
      chooseJob(j.jobId, fork);
    });
  };

  return <div className={"r8-rewind is-" + phase}>
    {demo ? <>
      <div className="r8-heading is-rewind"><h1>{heading}</h1></div>
      <RewindDeck stage={seq.stage} run={seq.run} policy={policy} onPolicy={setPolicy} onStart={startDemo} onReset={() => { playback.stop(); seq.reset(); }} onComplete={() => seq.advance("analysing")} />
      <div className="r8-rewind-errors"><ErrorText error={op.error || jobError} /></div>
    </> : <>
    <div className="r8-scenario-bar">
      <span className="r8-chip is-tonal">{demo ? "PREPARED SCENARIO" : sourceVerified ? "VERIFIED SOURCE" : "LIVE SOURCE"} · RUN {state.runId.slice(0, 8).toUpperCase()}</span>
      <div className="r8-scenario-meta"><span className="r8-eyebrow">SOURCE · {demo ? "ORIGINAL HISTORY" : sourceVerified ? "CLOSED LEDGER" : "OPEN LEDGER"}</span>
        <span>Events 0–{state.lastPosition} · horizon {clock(horizon)} UTC · {sourcePolicy}</span></div>
      {(phase === "fork" || phase === "running") && <span className="r8-chip is-policy">{sourcePolicy} <b>→</b> {verified?.alternativePolicy ?? policy}</span>}
      {verified && <span className="r8-chip is-coral" title={verified.childTimelineId}>{verified.sourcePolicy} → CHILD {verified.childTimelineId.slice(0, 8)} · {verified.alternativePolicy}</span>}
      <div className="r8-scenario-actions">
        {sourceVerified ? <>
          <label className="r8-inline-label">Alternative policy <select value={policy} onChange={e => setPolicy(e.target.value)}><option value="policy-v8">policy-v8 · {demo ? "Prepared alternate policy" : "V8 rules + pinned R5"}</option></select></label>
          <label className="r8-inline-label"><span className="r8-sr">Comparison mode</span><select aria-label="Comparison mode" value={mode} onChange={e => setMode(e.target.value)}>
            <option value="policy">Policy only</option><option value="intervention">Policy + sandbox intervention</option></select></label>
          <button className={"r8-btn " + (verified ? "" : "is-primary")} disabled={!canRun} title={blockReason ?? undefined}
            onClick={() => void op.run(JSON.stringify(payload), async key => {
              const j = await unwrap(api.post("/revenant/r8/runs/{id}/counterfactual", { path: { id: state.runId }, body: payload, idempotencyKey: key }));
              chooseJob(j.jobId, payload.forkPosition);
            })}>Run sandbox replay<Icon name="arrow" /></button>
        </> : <button className="r8-btn is-primary" disabled={op.busy} onClick={() => void op.run("close", async key => {
          await unwrap(api.post("/revenant/r8/runs/{id}/close", { path: { id: state.runId }, idempotencyKey: key })); await reload(); return demo ? "Prepared original ready" : "Source verified";
        })}>{demo ? (op.busy ? "Preparing source…" : "Prepare original") : op.busy ? "Verifying source…" : "Close and verify source"}</button>}
        <a className="r8-btn" href="#tower">Exit Rewind</a>
      </div>
    </div>
    {sourceVerified && mode === "intervention" && <div className="r8-intervention" role="group" aria-label="Sandbox intervention">
      <span className="r8-eyebrow">{demo ? "SANDBOX INTERVENTION · PREPARED REPLAY" : "SANDBOX INTERVENTION · REPLAY INPUT ONLY, NEVER A LIVE DECISION · FRESH MFA REQUIRED"}</span>
      <label className="r8-inline-label">Payment <select value={intent} onChange={e => setIntent(e.target.value)}><option value="">Choose payment</option>{state.payments.map(p => <option key={p.intentId}>{p.intentId}</option>)}</select></label>
      <label className="r8-inline-label">Analyst action <select value={action} onChange={e => setAction(e.target.value)}><option>Reject</option><option>Approve</option></select></label>
      <label className="r8-inline-label">Expected case version <input type="number" min="1" value={version} onChange={e => setVersion(Number(e.target.value))} /></label>
    </div>}

    <div className="r8-heading is-rewind">
      <h1>{heading}</h1>
      <div className="r8-rewind-status">
        {job ? <p role="status" data-testid="r8-job-status" className={"r8-job is-" + job.status.toLowerCase()}>Replay {demo && verified ? "complete" : job.status} {job.failure && `· ${job.failure}`}</p>
          : <p className="r8-heading-note">{blockReason ? blockReason.toUpperCase() : "FORK FIXED · AWAITING REPLAY"}</p>}
        {job && <ol className="r8-stepper" aria-label="Replay job states">{["Pending", "Running", "Verified"].map(s => <li key={s} className={stepClass(job.status, s)}>{demo && s === "Verified" ? "Complete" : s}</li>)}</ol>}
        {pending && job && <button className="r8-btn is-small" disabled={op.busy} onClick={() => void op.run("cancel:" + job.jobId, async key => {
          await unwrap(api.post("/revenant/r8/runs/{id}/counterfactual/{job}/cancel", { path: { id: state.runId, job: job.jobId }, idempotencyKey: key })); await reloadJob();
        })}>Cancel replay</button>}
      </div>
    </div>
    <div className="r8-rewind-errors"><ErrorText error={op.error || jobError} />{op.note && <p className="r8-ok" role="status">{op.note}</p>}
      {job?.status === "Failed" && <p className="r8-error" role="alert">The sandbox replay failed and produced no verified comparison. No KPI or child hash is shown.</p>}
      {job?.status === "Cancelled" && <p className="r8-error" role="alert">The sandbox replay was cancelled. No comparison was produced.</p>}</div>

    </>}

    {demo && demoVerified ? <RewindStage value={demoVerified} stage={seq.stage} alternate={alternate} forkAt={runAt} confirmedAt={atMinute(confirmedMin)} previewAt={atMinute(selectedMin)}
      pulse={burst} reveal={playback.progress} markers={chartMarkers(state)} advance={seq.advance} />
      : verified ? <Comparison value={verified} opening={state.opening} forkAt={forkMarker?.atMs ?? null} sourcePolicy={sourcePolicy} markers={demo ? chartMarkers(state) : undefined} />
      : <SourceHistory state={state} points={demo ? preparedHistory : liquidity.value?.actual ?? []} error={liquidity.error} loading={!demo && !liquidity.value && !liquidity.error}
        fork={preview ? eligible ?? null : null} policy={sourcePolicy} />}

    {demo ? <DemoTimeline selected={selectedMin} confirmed={confirmedMin} armed={forkReady} locked={sequencing} burst={burst} playing={playback.playing} progress={playback.progress}
      onSelect={setSelectedMin} onFork={() => { setConfirmedMin(selectedMin); setBurst(b => b + 1); }} onPlayPause={playback.toggle} />
    : <div className="r8-dock">
      <div className="r8-dock-glass"><Hourglass className="r8-dock-hourglass" level={fractionOf(position, state.lastPosition)} /></div>
      <div className="r8-dock-main">
        <div className="r8-dock-ticks" ref={ticksRef} aria-hidden="true">{dockMarkers.map(m => {
          const offset = markerOffsets.get(m.position) ?? 0;
          return <span key={m.position} style={{ left: fractionOf(m.position, state.lastPosition) * 100 + "%", "--marker-offset": `${offset}px` } as CSSProperties}
            className={m.position === position ? "is-active" : ""} data-offset={offset !== 0}>{clock(m.atMs).slice(0, 5)}</span>;
        })}</div>
        <TimelineScrubber adapter={adapter} position={position} markerOffsetPx={p => markerOffsets.get(p) ?? 0}
          onVerified={v => { setPosition(v.position); setPreview(v.state); }} />
      </div>
      <div className="r8-dock-side">
        <div className="r8-dock-time">{preview ? clock(preview.boundaryMs) : "--:--:--"}</div>
        {preview ? <p className="r8-dock-preview" data-testid="r8-preview">{demo ? "Prepared event" : "Verified event"} {preview.position} · {time(preview.boundaryMs)} · <code>{preview.stateHash.slice(0, 16)}…</code></p>
          : <p className="r8-dock-preview">{demo ? "Pick 09:00 UTC or another seal" : "Seek or pick a seal marker"}</p>}
        <span className={"r8-dock-fork" + (forkMarker ? " is-set" : "")} title={forkMarker ? `Event ${forkMarker.position}` : undefined}><Icon name="fork" />{forkMarker ? `Fork ${clock(forkMarker.atMs)}` : "No fork set"}</span>
        <div className={"r8-seal" + (auditOk ? " is-ok" : "")}>
          <strong>{demo ? "PREPARED ORIGINAL HISTORY" : !sourceVerified ? "LIVE SOURCE · NOT YET CLOSED" : audit.error ? "SOURCE VERIFICATION UNAVAILABLE" : !audit.value ? "VERIFYING SOURCE…"
            : unchanged ? "✓ ORIGINAL ARCHIVE UNCHANGED" : auditOk ? "✓ SOURCE CHAIN VERIFIED" : "SOURCE VERIFICATION FAILED"}</strong>
          <span>{audit.value ? `record ${shortHash(audit.value.recordHash)} · read only` : "read only · source never overwritten"}</span>
        </div>
      </div>
    </div>}
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

function dockMarkerOffsets(markers: S["R8Marker"][], end: string, width: number) {
  const anchors = markers.map(m => ({ position: m.position, x: fractionOf(m.position, end) * width })).sort((a, b) => a.x - b.x);
  // Keep enough room for a full HH:mm label and its button. Pack dense groups
  // back from the right, then keep any group at the left inside the track.
  const spacing = Math.min(36, width / Math.max(1, anchors.length - 1));
  const offsets = new Map<string, number>();
  let limit = width;
  for (let i = anchors.length - 1; i >= 0; i--) {
    const { position, x } = anchors[i]!;
    const center = Math.max(i * spacing, Math.min(x, limit));
    offsets.set(position, center - x);
    limit = center - spacing;
  }
  return offsets;
}

function chartMarkers(state: Snapshot): LaneMarker[] {
  return state.markers.map(m => ({ at: m.atMs, kind: m.kind, label: `Event ${m.position} · ${m.kind}${m.intentId ? " · " + m.intentId : ""} · ${time(m.atMs)}` }));
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
  const bank = chosen ?? (demo ? preparedRewind.bank : defaultBank);
  const network = demo && bank === preparedRewind.bank;
  const values = points.filter(x => x.balance.bankId === bank).map(x => ({ at: x.atMs, minor: x.balance.availableMinor }));
  const markers = chartMarkers(state);
  return <section className="r8-card r8-rewind-chart" aria-label="Source history">
    <div className="r8-card-head"><h2 className="r8-eyebrow">{network ? "Network position" : bankAlias(bank).name + " available balance"} · {fork ? "decision point" : "original history"} {!network && <small>{bank}</small>}</h2>
      <div className="r8-chips"><BankSelect value={bank} onChange={setChosen} network={demo} /><span className="r8-chip">ORIGINAL · {policy}</span></div></div>
    <div className="r8-rewind-plot">
      {error ? <p className="r8-error" role="alert">{error}</p> : loading ? <p className="r8-loading" role="status">Loading source observations…</p> :
        <TimeChart label={`${bank} source history available balance`} yLabel={network ? "USD · network position" : "USD · committed available balance"} markers={markers} exactMoney={amount}
          fork={fork ? { at: fork.atMs, label: `FORK ${clock(fork.atMs)}` } : null}
          callouts={values.length ? [{ series: "Original", label: `ORIGINAL · ${policy}` }] : []}
          series={[{ name: "Original", tone: "source", area: true, values }]} />}
    </div>
    <div className="r8-chart-foot"><p className="r8-muted">{demo ? "Prepared policy v7 history · 06:00–12:00 UTC" : `Per-bank committed observations at completed seals · ${state.status === "Verified" ? "completed source, read-only" : "live source, still open"}`} · lane: ▲ screening · ● other facts · | seals</p>
      <Drawer summary="View as table" className="is-up"><Table caption={`${bank} source available balance`} headers={["Seal time", "Available"]} rows={values.map(v => [time(v.at), amount(v.minor)])} /></Drawer></div>
  </section>;
}

function Comparison({ value, opening, forkAt, sourcePolicy, markers }: { value: Verified; opening: Balance[]; forkAt: number | null; sourcePolicy: string; markers?: LaneMarker[] }) {
  const differing = BANKS.find(b => value.balances.some(x => x.bankId === b.id && x.source.availableMinor !== x.alternative.availableMinor))?.id;
  const [bank, setBank] = useState(demo ? preparedRewind.bank : differing ?? value.balances[0]?.bankId ?? "BANK-A");
  const network = demo && bank === preparedRewind.bank;
  const [field, setField] = useState<Field>("availableMinor");
  const b = value.balances.filter(x => x.bankId === bank);
  const reference = !demo && field === "availableMinor" ? bufferReference(opening, bank) : undefined;
  const firstDiff = b.find(x => x.source[field] !== x.alternative[field])?.atMs ?? null;
  const wedgeFrom = forkAt ?? firstDiff;
  const alternative = demo ? "Prepared alternate" : "Verified sandbox";
  const series: Series[] = [
    { name: "Source history", tone: "source", area: true, values: b.map(x => ({ at: x.atMs, minor: x.source[field] })) },
    { name: alternative, tone: "alternative", values: b.filter(x => !demo || forkAt === null || x.atMs >= forkAt)
      .map(x => ({ at: x.atMs, minor: x.alternative[field] })) },
  ];
  const metric = (x: S["R8Headline"], key: "source" | "alternative" | "delta") => x.unit === "USD minor units" ? amount(x[key], key === "delta" && (!demo || /net/i.test(x.label))) : !demo && key === "delta" ? signedInteger(x[key]) : x[key];
  const heroIndex = Math.max(0, value.headlines.findIndex(h => /net/i.test(h.label)));
  return <div className="r8-compare">
    <section className="r8-card r8-rewind-chart" aria-label={demo ? "Prepared financial comparison" : "Verified financial comparison"}>
      <div className="r8-card-head"><h2 className="r8-eyebrow">{network ? "Network position" : bankAlias(bank).name + " " + fieldName[field].toLowerCase()} · {demo ? "original vs prepared alternate" : "source vs executed child"} {!network && <small>{bank}</small>}</h2>
        <div className="r8-chips"><BankSelect value={bank} onChange={setBank} network={demo} />
          <label className="r8-select"><span className="r8-sr">Balance</span><select aria-label="Balance" value={field} onChange={e => setField(e.target.value as Field)}>
            <option value="availableMinor">Available</option><option value="postedMinor">Posted</option><option value="reservedMinor">Reserved</option></select></label>
          <span className="r8-chip">ORIGINAL · {value.sourcePolicy}</span><span className="r8-chip is-coral">{demo ? "ALTERNATE" : "CHILD"} · {value.alternativePolicy}</span></div></div>
      <div className="r8-rewind-plot">
        <TimeChart reference={reference} showNote={false} label={bank + " source and completed alternative balances"} yLabel={network ? "USD · network position" : `USD · committed ${fieldName[field].toLowerCase()} balance`} markers={markers} exactMoney={amount}
          fork={forkAt !== null ? { at: forkAt, label: `FORK ${clock(forkAt)}` } : null}
          wedge={wedgeFrom !== null ? { a: alternative, b: "Source history", from: wedgeFrom } : null}
          callouts={b.length ? [{ series: alternative, label: `ALT · ${value.alternativePolicy}` }, { series: "Source history", label: `ORIGINAL · ${value.sourcePolicy}` }] : []}
          series={series} />
      </div>
      <div className="r8-chart-foot">
        <p className="r8-muted">{demo ? "Illustrative policy comparison" : "Source history and executed sandbox child"} · {value.mode === "PolicyOnly" ? "policy only" : "policy + sandbox intervention"} · {forkAt === null ? (firstDiff !== null ? `shading from first observed difference ${clock(firstDiff)}` : "no observed difference for this bank") : "compared segment from the fork"} · curve endpoint difference ≠ net economic effect</p>
        <div className="r8-drawers">
          <Drawer summary="Aligned balance data" className="is-up">
            {reference && <p>Shared operating buffer reference: {reference.status === "Available" ? money(reference.minor) : "Unavailable"}</p>}
            {reference && <BufferNote reference={reference} placement={placementOf(series, reference)} />}
            <Table caption={`${bank} aligned balances`} headers={["Time", "Source", "Alternative"]} rows={b.map(x => [time(x.atMs), amount(x.source[field]), amount(x.alternative[field])])} />
          </Drawer>
          <Drawer summary="Outcome breakdown" className="is-up">
            <Table headers={["Outcome", "Source", "Alternative", "Delta"]} rows={value.breakdown.map(x => [x.label, metric(x, "source"), metric(x, "alternative"), metric(x, "delta")])} />
            <p className="r8-muted">{value.costPolicy} · {value.continuationPolicy}</p>
          </Drawer>
          <Drawer summary="Inspect underlying payments and evidence" className="is-up">
            <Table headers={["Intent", "Principal", "Source", "Alternative", "Cause", "Delay", "Evidence"]} rows={value.payments.map(p => [
              p.intentId, amount(p.principalMinor), p.sourceStatus, p.alternativeStatus, p.alternativeCause, p.delayMinutes + " min",
              <span className="r8-mono" title={`${p.sourceEvidence ?? "Unavailable"} / ${p.alternativeEvidence ?? "Unavailable"}`}>{shortHash(p.sourceEvidence)} / {shortHash(p.alternativeEvidence)}</span>])} />
            {(value.actions ?? []).length > 0 && <Table headers={["Intent", "Reviewer", "Time", "Action", "Disposition"]} rows={(value.actions ?? []).map(a => [a.intentId, a.actor, time(a.atMs), a.action, a.status + " · " + a.code])} />}
          </Drawer>
        </div>
      </div>
    </section>
    <section className="r8-kpi-col" aria-label={demo ? "Prepared economic outcome" : "Verified economic outcome"}>
      {value.headlines.map((x, i) => <div key={x.label} data-testid="r8-kpi" data-delta={x.delta} className={"r8-quad" + (i === heroIndex ? " is-hero" : "") + " is-k" + i}
        style={{ order: i === heroIndex ? -1 : i }}>
        <div className="r8-eyebrow">{x.label}</div>
        <p className="r8-figure">{metric(x, "delta")}{(!demo || x.unit === "payments") && <small>{x.unit === "USD minor units" ? "USD" : x.unit}</small>}</p>
        <span className="r8-quad-delta">{metric(x, "source")} → {metric(x, "alternative")}</span>
        <span className="r8-quad-context">{demo ? "Illustrative alternative-policy estimate" : kpiContext(x.label, value)}</span>
      </div>)}
      <p className="r8-integrity-line">{demo ? "Prepared scenario" : "Verified branch"} <code title={value.childTimelineId}>{value.childTimelineId}</code> · record {shortHash(value.childRecordHash)}{!demo && ` · source ${sourcePolicy} unchanged by construction`}</p>
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
