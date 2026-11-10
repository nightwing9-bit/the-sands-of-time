// Ghost Lap V1 greybox (spec 06, task 6.5): play a solo run, reopen the verified original, rewind with the one shared scrubber, fork
// ("Rewrite") at a nonterminal tick and play the branch against the inert source ghost with an aligned score delta. Every number shown
// is a server answer; this screen holds only what the user chose (which view, which tick) and which keys are down.
import { useEffect, useMemo, useRef, useState } from "react";
import { describeFailure, type MarrowTransport, type TimelinePosition, type TransportFailure } from "@sands/contracts";
import { TimelineScrubber, type VerifiedPreview } from "@sands/timeline-ui";
import { ArenaView } from "./ArenaView.tsx";
import type { GreyboxWire } from "./greyboxWire.ts";
import { failureOf, useKeyboardIntents, useLiveFrames } from "./liveSession.ts";
import { createOriginalAdapter } from "./originalAdapter.ts";
import type { AlignedView, Arena, LiveFrame, OriginalDescriptor, WorldView } from "./types.ts";
import "./greybox.css";

type Route = { view: "start" } | { view: "original"; id: string } | { view: "branch"; id: string };

function readRoute(): Route {
  const q = new URLSearchParams(window.location.search);
  const original = q.get("original");
  const branch = q.get("branch");
  if (branch) return { view: "branch", id: branch };
  if (original) return { view: "original", id: original };
  return { view: "start" };
}

function useRoute(): [Route, (r: Route) => void] {
  const [route, setRoute] = useState<Route>(readRoute);
  useEffect(() => {
    const on = () => setRoute(readRoute());
    window.addEventListener("popstate", on);
    return () => window.removeEventListener("popstate", on);
  }, []);
  const go = (r: Route) => {
    const q = r.view === "start" ? "" : `?${r.view}=${encodeURIComponent(r.id)}`;
    window.history.pushState(null, "", `${window.location.pathname}${q}`);
    setRoute(r);
  };
  return [route, go];
}

const clock = (ticks: number, rate: number) => {
  const s = Math.max(0, Math.floor(ticks / rate));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/** Text and data readout of the world the arena draws: accessible, and what browser tests read instead of canvas pixels. */
function WorldReadout({ world, label }: { world: WorldView; label: string }) {
  const p = world.players[0]!; // the wire rejects a world without players
  const candles = world.activeCandles.map((c) => `${c.id}:${Math.round(c.x)}:${Math.round(c.y)}`).join(";");
  return (
    <output
      className="readout"
      data-testid={`${label}-readout`}
      data-tick={world.tick}
      data-x={p.x}
      data-y={p.y}
      data-charge={p.charge}
      data-score={p.totalScore}
      data-pickups={p.pickups}
      data-collected={world.collectedCount}
      data-candles={candles}
      data-state-hash={world.stateHash ?? ""}
    >
      Tick {world.tick} · player at ({Math.round(p.x)}, {Math.round(p.y)}) · {world.activeCandles.length} candles lit
    </output>
  );
}

function ScoreHud({ world, arena, title }: { world: WorldView; arena: Arena; title: string }) {
  const p = world.players[0]!; // the wire rejects a world without players
  return (
    <section className="hud" aria-label={title} data-testid="hud">
      <span className="hud-time" data-testid="hud-time">{clock(arena.roundTicks - world.tick, arena.tickRate)} left</span>
      <span data-testid="hud-score">Score {p.totalScore}</span>
      <span className="hud-breakdown">move {p.movementPoints} · candles {p.pickupPoints} · penalty {p.penaltyPoints}</span>
      <span data-testid="hud-charge" data-charge={p.charge}>
        Soulfire {p.charge}/{arena.maxCharge}{p.overlit ? ` · OVERLIT, Burst ready (Space) until tick ${p.overlitDeadlineTick}` : ""}
      </span>
      <span data-testid="hud-pickups">Candles {p.pickups}</span>
      <span>Bursts {p.bursts}</span>
    </section>
  );
}

function AlignedHud({ aligned, rate }: { aligned: AlignedView; rate: number }) {
  const d = aligned.delta;
  const pill = d > 0 ? `+${d} AHEAD` : d < 0 ? `\u2212${-d} BEHIND` : "EVEN";
  return (
    <section className="compare-hud" aria-label="Past and present at the same time" data-testid="compare-hud" data-tick={aligned.tick}
      data-ghost-score={aligned.ghostScore} data-current-score={aligned.currentScore} data-delta={d} data-final={aligned.isFinal ? "true" : "false"}>
      <span className="past">PAST YOU {aligned.ghostScore}</span>
      <span className="when">{aligned.isFinal ? "FINAL" : `at ${clock(aligned.tick, rate)} (tick ${aligned.tick})`}</span>
      <span className="now">YOU NOW {aligned.currentScore}</span>
      <span className={`delta ${d > 0 ? "ahead" : d < 0 ? "behind" : ""}`} data-testid="delta-pill">{pill}</span>
    </section>
  );
}

function Failure({ failure, testId }: { failure: TransportFailure; testId: string }) {
  return <p role="alert" data-testid={testId} data-kind={failure.kind}>{describeFailure(failure)}</p>;
}

function useArena(wire: GreyboxWire) {
  const [arena, setArena] = useState<Arena | null>(null);
  const [failure, setFailure] = useState<TransportFailure | null>(null);
  useEffect(() => {
    const c = new AbortController();
    void wire.arena(c.signal).then((a) => !c.signal.aborted && setArena(a), (e) => !c.signal.aborted && setFailure(failureOf(e)));
    return () => c.abort();
  }, [wire]);
  return { arena, failure };
}

// ---- Start + live original ------------------------------------------------------------------------------------------------------

function StartView({ wire, onOriginal }: { wire: GreyboxWire; onOriginal: (id: string) => void }) {
  const { arena, failure: arenaFailure } = useArena(wire);
  const [session, setSession] = useState<LiveFrame | null>(null);
  const [failure, setFailure] = useState<TransportFailure | null>(null);
  const [starting, setStarting] = useState(false);
  const key = useRef<string | null>(null);
  const begin = async () => {
    setStarting(true);
    setFailure(null);
    key.current ??= crypto.randomUUID();
    try {
      const first = await wire.startOriginal(42, 0, key.current);
      await wire.startLoop(first.sessionId);
      key.current = null;
      setSession(first);
    } catch (error) {
      setFailure(failureOf(error));
    } finally {
      setStarting(false);
    }
  };
  if (arenaFailure) return <Failure failure={arenaFailure} testId="arena-load-error" />;
  if (!arena) return <p role="status">Loading arena…</p>;
  if (session) return <LiveOriginal wire={wire} arena={arena} initial={session} onOriginal={onOriginal} />;
  return (
    <section className="start" data-testid="start-view">
      <p>Solo run · {arena.rulesVersion} / {arena.layoutVersion} · {clock(arena.roundTicks, arena.tickRate)}. Move with arrow keys or WASD; Space Bursts when Overlit.</p>
      <button type="button" onClick={() => void begin()} disabled={starting}>{starting ? "Starting…" : "Start solo run"}</button>
      {failure ? <Failure failure={failure} testId="start-error" /> : null}
    </section>
  );
}

function LiveOriginal({ wire, arena, initial, onOriginal }: { wire: GreyboxWire; arena: Arena; initial: LiveFrame; onOriginal: (id: string) => void }) {
  const { frame, failure } = useLiveFrames(wire, initial.sessionId, initial);
  const running = frame?.status === "Running";
  const answer = useKeyboardIntents(wire, initial.sessionId, frame, running);
  const completedId = frame?.status === "Complete" ? frame.completedId : null;
  const announced = useRef(false);
  useEffect(() => {
    // Only a server-completed, replay-verified original opens the replay; there is no optimistic "saved" state.
    if (completedId && !announced.current) {
      announced.current = true;
      onOriginal(completedId);
    }
  }, [completedId, onOriginal]);
  if (!frame) return null;
  return (
    <section data-testid="live-view" data-status={frame.status} data-session-id={frame.sessionId}>
      <p className="status" role="status" data-testid="live-status">
        {frame.status === "Running" ? "Recording your solo run on the server." : frame.status === "Completing" ? "Final tick reached. Saving and verifying the original…" : `Run ${frame.status.toLowerCase()}.`}
      </p>
      <ScoreHud world={frame.world} arena={arena} title="Solo run" />
      <ArenaView arena={arena} world={frame.world} playerRole="living" />
      <WorldReadout world={frame.world} label="live" />
      {frame.error ? <p role="alert" data-testid="live-error">{frame.error}</p> : null}
      {failure ? <Failure failure={failure} testId="frame-error" /> : null}
      {answer && !answer.accepted ? <p className="note" data-testid="input-rejected">Server rejected an input ({answer.rejection}); it resyncs automatically.</p> : null}
    </section>
  );
}

// ---- Replay of a verified original + Rewrite ------------------------------------------------------------------------------------

function ReplayView({ wire, id, onBranch }: { wire: GreyboxWire; id: string; onBranch: (branchId: string) => void }) {
  const [descriptor, setDescriptor] = useState<OriginalDescriptor | null>(null);
  const [failure, setFailure] = useState<TransportFailure | null>(null);
  useEffect(() => {
    const c = new AbortController();
    setDescriptor(null);
    void wire.original(id, c.signal).then((d) => !c.signal.aborted && setDescriptor(d), (e) => !c.signal.aborted && setFailure(failureOf(e)));
    return () => c.abort();
  }, [wire, id]);
  if (failure) return <Failure failure={failure} testId="original-error" />;
  if (!descriptor) return <p role="status">Opening and verifying the original…</p>;
  return <Replay key={descriptor.timelineId} wire={wire} descriptor={descriptor} onBranch={onBranch} />;
}

function Replay({ wire, descriptor, onBranch }: { wire: GreyboxWire; descriptor: OriginalDescriptor; onBranch: (branchId: string) => void }) {
  const adapter = useMemo(() => createOriginalAdapter(descriptor, wire), [descriptor, wire]);
  const [position, setPosition] = useState<TimelinePosition>(descriptor.bounds.start);
  const [preview, setPreview] = useState<VerifiedPreview<WorldView> | null>(null);
  return (
    <section data-testid="replay-view" data-original-id={descriptor.timelineId}>
      <p className="stamp" data-testid="original-stamp" data-final-score={descriptor.finalScore} data-final-hash={descriptor.finalStateHash}>
        ORIGINAL RUN PRESERVED · {descriptor.historyStatus} · final score {descriptor.finalScore} · hash {descriptor.finalStateHash.slice(0, 12)}…
      </p>
      {preview ? <ScoreHud world={preview.state} arena={descriptor.arena} title="Original at this tick" /> : <p data-testid="hud">No tick verified by the server yet.</p>}
      <ArenaView arena={descriptor.arena} world={preview?.state ?? null} playerRole="original" />
      {preview ? <WorldReadout world={preview.state} label="replay" /> : null}
      <TimelineScrubber
        adapter={adapter}
        position={position}
        step="1"
        pageStep="30"
        onVerified={(p) => { setPosition(p.position); setPreview(p); }}
        onForked={(result) => onBranch(result.branchId)}
      />
      <p className="note">“Fork from here” rewrites from the verified tick: a new branch, the original stays as it is. The final tick can be inspected but not forked.</p>
    </section>
  );
}

// ---- Branch challenge against the inert ghost -----------------------------------------------------------------------------------

function ChallengeView({ wire, id }: { wire: GreyboxWire; id: string }) {
  const { arena, failure: arenaFailure } = useArena(wire);
  const { frame, failure } = useLiveFrames(wire, id, null);
  const running = frame?.status === "Running";
  useKeyboardIntents(wire, id, frame, running);
  const [startFailure, setStartFailure] = useState<TransportFailure | null>(null);
  if (arenaFailure) return <Failure failure={arenaFailure} testId="arena-load-error" />;
  if (failure && !frame) return <Failure failure={failure} testId="branch-error" />;
  if (!arena || !frame) return <p role="status">Loading branch…</p>;
  const p = frame.world.players[0]!;
  return (
    <section data-testid="challenge-view" data-status={frame.status} data-branch-id={frame.sessionId} data-source-id={frame.sourceId ?? ""} data-fork-tick={frame.forkTick ?? ""}>
      <p className="stamp">REWRITE from tick {frame.forkTick} · ORIGINAL RUN PRESERVED (source {frame.sourceId?.slice(0, 8)}…)</p>
      {frame.status === "Ready" ? (
        <div className="ready" data-testid="inherited">
          <p>Inherited at tick {frame.world.tick}: score {p.totalScore} · Soulfire {p.charge} · {frame.world.collectedCount} candles consumed.</p>
          <button type="button" onClick={() => void wire.startLoop(id).then(() => setStartFailure(null), (e) => setStartFailure(failureOf(e)))}>Start rewrite</button>
          {startFailure ? <Failure failure={startFailure} testId="branch-start-error" /> : null}
        </div>
      ) : (
        <p className="status" role="status" data-testid="live-status">{frame.status === "Running" ? "Rewriting on the server. The cyan ghost is the original run; it cannot collect, touch or score." : `Branch ${frame.status.toLowerCase()}.`}</p>
      )}
      {frame.aligned ? <AlignedHud aligned={frame.aligned} rate={frame.tickRate} /> : null}
      <ScoreHud world={frame.world} arena={arena} title="Current branch" />
      <ArenaView arena={arena} world={frame.world} ghost={frame.ghost} playerRole="living" />
      <WorldReadout world={frame.world} label="live" />
      {frame.ghost ? (
        <output data-testid="ghost-readout" data-tick={frame.ghost.tick} data-x={frame.ghost.x} data-y={frame.ghost.y} data-score={frame.ghost.totalScore} data-pickups={frame.ghost.pickups} className="readout">
          Ghost at ({Math.round(frame.ghost.x)}, {Math.round(frame.ghost.y)}) · {frame.ghost.pickups} candles in its own history
        </output>
      ) : null}
      {frame.error ? <p role="alert" data-testid="live-error">{frame.error}</p> : null}
    </section>
  );
}

// ---- Shell ----------------------------------------------------------------------------------------------------------------------

// The verified guest session owns every run. Reuse an existing one (a reload must keep the same owner), else start exactly one,
// even when React mounts the effect twice (StrictMode).
let guestOnce: Promise<"ready" | TransportFailure> | null = null;
function ensureGuest(transport: MarrowTransport): Promise<"ready" | TransportFailure> {
  guestOnce ??= (async () => {
    const me = await transport.api.get("/auth/guest/me");
    if (me.ok) return "ready" as const;
    const started = await transport.api.post("/auth/guest/start");
    return started.ok ? ("ready" as const) : started.failure;
  })();
  return guestOnce;
}

export function GhostLapGreybox({ transport, wire }: { transport: MarrowTransport; wire: GreyboxWire }) {
  const [route, go] = useRoute();
  const [guest, setGuest] = useState<"checking" | "ready" | TransportFailure>("checking");
  useEffect(() => {
    let alive = true;
    void ensureGuest(transport).then((g) => alive && setGuest(g));
    return () => {
      alive = false;
    };
  }, [transport]);
  if (guest === "checking") return <p role="status">Starting a guest session…</p>;
  if (guest !== "ready") return <Failure failure={guest} testId="guest-error" />;
  return (
    <div className="greybox" data-testid="ghost-lap-greybox">
      <nav>
        <button type="button" onClick={() => go({ view: "start" })}>New solo run</button>
      </nav>
      {route.view === "start" ? <StartView wire={wire} onOriginal={(id) => go({ view: "original", id })} /> : null}
      {route.view === "original" ? <ReplayView wire={wire} id={route.id} onBranch={(id) => go({ view: "branch", id })} /> : null}
      {route.view === "branch" ? <ChallengeView key={route.id} wire={wire} id={route.id} /> : null}
    </div>
  );
}
