// Revenant V1 thin slice screen (spec 05, task 5.4): six banks, source events, scrub (the shared scrubber), policy selector and
// branch comparison. Everything displayed is a server answer; the screen holds only what the user chose (seed, policy, position).
import { useEffect, useMemo, useRef, useState } from "react";
import { describeFailure, type MarrowTransport, type TimelinePosition, type TransportFailure } from "@sands/contracts";
import { TimelineScrubber, type VerifiedPreview } from "@sands/timeline-ui";
import { BanksPanel, ComparisonPanel, EventsPanel, PolicySelector, TruthBanner } from "./panels.tsx";
import { createSliceAdapter } from "./sliceAdapter.ts";
import type { SliceWire } from "./sliceWire.ts";
import type { BranchOptions, RunDescriptor, SliceComparison, SliceState } from "./types.ts";
import "./slice.css";

const failureOf = (error: unknown): TransportFailure =>
  typeof error === "object" && error !== null && "failure" in error ? ((error as { failure: TransportFailure }).failure) : { kind: "server", status: 0 };

type Start = { kind: "idle" } | { kind: "starting" } | { kind: "failed"; failure: TransportFailure } | { kind: "ready"; run: RunDescriptor };

export function FridaySlice({ transport, wire }: { transport: MarrowTransport; wire: SliceWire }) {
  const [seedText, setSeedText] = useState("42");
  const [start, setStart] = useState<Start>({ kind: "idle" });
  const startKey = useRef<string | null>(null);
  const seed = /^\d{1,7}$/.test(seedText) ? Number(seedText) : null;
  const begin = async () => {
    if (seed === null) return;
    setStart({ kind: "starting" });
    // A guest session is the verified principal that owns the run. The key is kept until success so a retry is idempotent.
    const session = await transport.api.post("/auth/guest/start");
    if (!session.ok) return setStart({ kind: "failed", failure: session.failure });
    startKey.current ??= crypto.randomUUID();
    try {
      const run = await wire.startRun(seed, startKey.current);
      startKey.current = null;
      setStart({ kind: "ready", run });
    } catch (error) {
      setStart({ kind: "failed", failure: failureOf(error) });
    }
  };
  return (
    <div data-testid="friday-slice">
      <section className="start" aria-label="Start a synthetic Friday Ring run">
        <label>
          Scenario seed{" "}
          <input aria-label="Scenario seed" data-testid="seed-input" inputMode="numeric" value={seedText} onChange={(e) => setSeedText(e.target.value)} disabled={start.kind === "starting"} />
        </label>
        <button type="button" onClick={() => void begin()} disabled={seed === null || start.kind === "starting"}>
          {start.kind === "ready" ? "Start another synthetic Friday Ring run" : "Start synthetic Friday Ring run"}
        </button>
        {seed === null ? <span role="note">Seed must be a whole number of up to 7 digits.</span> : null}
        {start.kind === "starting" ? <span role="status" data-testid="run-starting">Recording the run through the Marrow Timeline…</span> : null}
        {start.kind === "failed" ? <span role="alert" data-testid="run-error" data-kind={start.failure.kind}>{describeFailure(start.failure)}</span> : null}
      </section>
      {start.kind === "ready" ? <RunView key={start.run.timelineId} run={start.run} wire={wire} /> : null}
    </div>
  );
}

function RunView({ run, wire }: { run: RunDescriptor; wire: SliceWire }) {
  const adapter = useMemo(() => createSliceAdapter(run, wire), [run, wire]);
  const [position, setPosition] = useState<TimelinePosition>(run.bounds.start);
  const [state, setState] = useState<SliceState | null>(null);
  const [stateError, setStateError] = useState<TransportFailure | null>(null);
  const [policyId, setPolicyId] = useState<string>(() => run.policies.find((p) => p.policyId !== run.sourcePolicyId)?.policyId ?? run.sourcePolicyId);
  const [branch, setBranch] = useState<{ kind: "none" } | { kind: "loading"; branchId: string } | { kind: "failed"; failure: TransportFailure } | { kind: "ready"; comparison: SliceComparison }>({ kind: "none" });
  const forkOptions = useMemo<BranchOptions>(() => ({ policyId }), [policyId]);
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    return () => { alive.current = false; };
  }, []);
  // The opening state is also a server answer: restore the first position so the banks are never shown from the description alone.
  useEffect(() => {
    const controller = new AbortController();
    wire.restore(run.timelineId, run.bounds.start, controller.signal).then(
      (answer) => { if (!controller.signal.aborted) { setPosition(answer.position); setState(answer.state); } },
      (error: unknown) => { if (!controller.signal.aborted) setStateError(failureOf(error)); },
    );
    return () => controller.abort();
  }, [run, wire]);
  const onVerified = (preview: VerifiedPreview<SliceState>) => {
    setPosition(preview.position);
    setState(preview.state);
    setStateError(null);
  };
  const onForked = (result: { branchId: string }) => {
    setBranch({ kind: "loading", branchId: result.branchId });
    wire.comparison(result.branchId).then(
      (comparison) => { if (alive.current) setBranch({ kind: "ready", comparison }); },
      (error: unknown) => { if (alive.current) setBranch({ kind: "failed", failure: failureOf(error) }); },
    );
  };
  return (
    <>
      <TruthBanner labelling={run.labelling} policyId={run.sourcePolicyId} />
      <p className="meta" data-testid="run-meta" data-run-id={run.runId} data-timeline-id={run.timelineId}>
        {run.scenario.scenarioId} {run.scenario.scenarioVersion}, seed {run.scenario.seed}, history {run.historyStatus}. Source state hash <span className="mono">{run.sourceFinalStateHash.slice(0, 16)}…</span>
      </p>
      <section aria-label="Scrub" data-testid="scrub-section">
        <h2>Select a financial event position</h2>
        <TimelineScrubber adapter={adapter} position={position} onVerified={onVerified} forkOptions={forkOptions} onForked={onForked} />
        <PolicySelector policies={run.policies} value={policyId} onChange={setPolicyId} disabled={false} />
        <p className="sub" data-testid="fork-help">
          “Fork from here” replays the same recorded payment intents after the selected event under {policyId} and compares the result with the original {run.sourcePolicyId}.
          The original history is never modified.
        </p>
        {stateError ? <p role="alert" data-testid="state-error" data-kind={stateError.kind}>{describeFailure(stateError)}</p> : null}
        <p data-testid="verified-position" data-position={position}>{state ? `Showing the state the server verified at event ${position}.` : "No position verified by the server yet."}</p>
      </section>
      <div className="grid">
        <BanksPanel run={run} state={state} />
        <EventsPanel run={run} state={state} position={position} />
      </div>
      {branch.kind === "loading" ? <p role="status" data-testid="comparison-loading">Branch {branch.branchId} recorded. Loading the aligned comparison…</p> : null}
      {branch.kind === "failed" ? <p role="alert" data-testid="comparison-error" data-kind={branch.failure.kind}>{describeFailure(branch.failure)}</p> : null}
      {branch.kind === "ready" ? <ComparisonPanel comparison={branch.comparison} /> : null}
    </>
  );
}
