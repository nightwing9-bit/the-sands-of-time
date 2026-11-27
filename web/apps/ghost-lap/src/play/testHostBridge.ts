// DEV-ONLY bridge: PlayTransport over the TEST-HOST greybox routes (/ghost-lap/*, tests/E2E/backend). NOT the production transport.
//
// Why it exists: the production hub adapter (/hubs/ghostlap, /hubs/ghostlap-solo) is an external dependency that does not exist yet
// (docs/architecture/22-ghostlap-spec13-4-day-play.md lists exactly what is missing). This bridge lets a person drive a REAL authoritative
// ghost-v1 solo simulation (the C# reducer, 30 Hz tick loop, real candle plan, real scoring and Burst) in the browser today. It adds no
// rule and no authority on the client.
//
// What it is NOT, and the HUD says so:
//   * not WebSocket/SignalR: frames are polled over HTTP and inputs are POSTed one at a time, so input->screen latency and frame spacing
//     here are properties of THIS bridge, not of the production feed. Feel judged on it is provisional.
//   * not complete in events: a poll can skip a tick, and with it that tick's events (eventsComplete=false).
//   * single player only (slot 0), no spectator or controller-epoch semantics.
// Never mounted in a production build (callers gate on import.meta.env.DEV).
import { createMarrowTransport, describeHandshake, type MarrowTransport, type TransportFailure } from "@sands/contracts";
import { createGreyboxHttpWire, type GreyboxWire } from "../greybox/greyboxWire.ts";
import { failureOf } from "../greybox/liveSession.ts";
import type { Arena, LiveFrame, WorldEvent } from "../greybox/types.ts";
import { ARENA_V1_RENDER } from "../arena/arenaV1Descriptor.ts";
import type { ActorSlot, Charge, ControlIntent } from "../presentation/types.ts";
import type { CandleFrame, ConnectionState, InputAnswerView, PlayEvent, PlayEventKind, PlayFrame, PlayTransport } from "./playTypes.ts";

const POLL_GAP_MS = 10; // pause after each answered poll; the server ticks at 30 Hz so most polls repeat the last tick and are dropped as duplicates
const KEEPALIVE_MS = 90; // ghost-v1 holds a vector for at most six ticks (200 ms) without a newer input; precedent from the greybox
const DEGRADED_AFTER_FAILURES = 2;
// InputAdmission documents CommitPending as "the client resubmits for the following tick", and a timing refusal is repaired by re-anchoring.
// A refused Burst PRESS is never merged away like movement, so the bridge re-sends it a bounded number of times, then reports it as refused.
const TRANSIENT_REFUSALS: ReadonlySet<string> = new Set(["commit_pending", "late_tick", "future_tick", "stale_sequence"]);
const BURST_RETRY_WINDOW_MS = 400;
const BURST_RETRY_MAX = 4;
const KNOWN: ReadonlySet<string> = new Set<PlayEventKind>(["CandleCollected", "OverlitStarted", "ContactDrain", "BurstManual", "BurstAuto", "BurstHit", "OverlitCancelled", "RoundFinished"]);

export interface BridgeOptions {
  readonly seed?: number;
  readonly mascotId?: number;
  readonly wire?: GreyboxWire;
  readonly transport?: MarrowTransport;
  readonly now?: () => number;
}

/** Same-origin guest cookie bootstrap (the greybox precedent: GET /auth/guest/me, else POST /auth/guest/start). */
async function ensureGuest(transport: MarrowTransport): Promise<TransportFailure | null> {
  const me = await transport.api.get("/auth/guest/me");
  if (me.ok) return null;
  const started = await transport.api.post("/auth/guest/start");
  return started.ok ? null : started.failure;
}

export function arenaMatchesRenderDescriptor(arena: Arena): boolean {
  const a = arena.obstacles;
  const b = ARENA_V1_RENDER.obstacles;
  return arena.width === ARENA_V1_RENDER.width && arena.height === ARENA_V1_RENDER.height && a.length === b.length &&
    a.every((r, i) => r.x0 === b[i]!.x0 && r.y0 === b[i]!.y0 && r.x1 === b[i]!.x1 && r.y1 === b[i]!.y1);
}

export function toPlayFrame(live: LiveFrame, arena: Arena, receivedAtMs: number): PlayFrame {
  const w = live.world;
  const candles: CandleFrame[] = [
    ...w.telegraphCandles.map((c) => ({ id: c.id, x: c.x, y: c.y, phase: "telegraph" as const })),
    ...w.activeCandles.map((c) => ({ id: c.id, x: c.x, y: c.y, phase: "active" as const })),
  ];
  // Identity of a solo event: the run, the tick and the position in that tick's ordered list (13.0 map A6). Not an invented UUID.
  const events: PlayEvent[] = w.events.map((e: WorldEvent, i: number) => ({
    key: `${live.sessionId}:${w.tick}:${i}`,
    tick: w.tick,
    kind: (KNOWN.has(e.kind) ? e.kind : "Unknown") as PlayEventKind,
    raw: e.kind,
    slot: e.slot,
    otherSlot: e.otherSlot,
    value: e.value,
    candleId: e.candleId,
  }));
  return {
    identity: live.sessionId,
    tick: w.tick,
    tickRate: live.tickRate,
    roundTicks: arena.roundTicks,
    final: w.isFinal,
    receivedAtMs,
    players: w.players.map((p) => ({
      slot: p.slot as ActorSlot,
      x: p.x,
      y: p.y,
      charge: p.charge as Charge,
      overlit: p.overlit,
      overlitDeadlineTick: p.overlit ? p.overlitDeadlineTick : null,
      totalScore: p.totalScore,
      movementPoints: p.movementPoints,
      pickupPoints: p.pickupPoints,
      penaltyPoints: p.penaltyPoints,
      pickups: p.pickups,
      bursts: p.bursts,
      appliedSeq: p.lastSequence,
    })),
    candles,
    events,
    you: { slot: 0, canControl: live.status === "Running" },
    eventsComplete: false,
  };
}

export class TestHostBridge implements PlayTransport {
  readonly label = "TEST-HOST BRIDGE (dev only, HTTP polling, real ghost-v1 authority, not the production hub)";
  readonly production = false;
  private readonly frameListeners = new Set<(f: PlayFrame) => void>();
  private readonly connectionListeners = new Set<(c: ConnectionState) => void>();
  private readonly answerListeners = new Set<(a: InputAnswerView) => void>();
  private readonly wire: GreyboxWire;
  private readonly transport: MarrowTransport;
  private readonly now: () => number;
  private sessionId: string | null = null;
  private arena: Arena | null = null;
  private seq = -1;
  private anchor = { tick: 0, at: 0, rate: 30 };
  private current: ControlIntent = { moveX: 0, moveY: 0, requestBurst: false };
  private dirty = false;
  private burstPress = false;
  private neutralPending = false;
  private burstRetry: { until: number; count: number } = { until: 0, count: 0 };
  private inflight = false;
  private keepalive: ReturnType<typeof setInterval> | undefined;
  private abort = new AbortController();
  private disposed = false;
  private lastGoodAt: number | null = null;

  private readonly options: BridgeOptions;

  constructor(options: BridgeOptions = {}) {
    this.options = options;
    this.wire = options.wire ?? createGreyboxHttpWire();
    this.transport = options.transport ?? createMarrowTransport({ apiBaseUrl: "", realtimeBaseUrl: "" });
    this.now = options.now ?? (() => performance.now());
  }

  onFrame(l: (f: PlayFrame) => void) { this.frameListeners.add(l); return () => void this.frameListeners.delete(l); }
  onConnection(l: (c: ConnectionState) => void) { this.connectionListeners.add(l); return () => void this.connectionListeners.delete(l); }
  onInputAnswer(l: (a: InputAnswerView) => void) { this.answerListeners.add(l); return () => void this.answerListeners.delete(l); }
  private setConnection(c: ConnectionState) { for (const l of this.connectionListeners) l(c); }

  async start(signal: AbortSignal): Promise<void> {
    signal.addEventListener("abort", () => this.dispose(), { once: true });
    this.setConnection({ kind: "connecting" });
    try {
      // The shared client refuses every call until the contract handshake proved the host compatible (same gate the greybox passes).
      const verdict = await this.transport.negotiate(this.abort.signal);
      if (!verdict.ok) return this.setConnection({ kind: "failed", reason: describeFailure(verdict.failure, "Could not check the server contract") });
      if (!verdict.value.compatible) return this.setConnection({ kind: "failed", reason: describeHandshake(verdict) });
      const guest = await ensureGuest(this.transport);
      if (guest) return this.setConnection({ kind: "failed", reason: describeFailure(guest, "Could not start a guest session") });
      const arena = await this.wire.arena(this.abort.signal);
      if (!arenaMatchesRenderDescriptor(arena)) {
        return this.setConnection({ kind: "failed", reason: "The host's arena does not match the arena-v1 render descriptor. Drawing walls from it would be wrong." });
      }
      this.arena = arena;
      const seed = this.options.seed ?? Math.floor(Math.random() * 1_000_000);
      const key = `play-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`; // the established OperationKey, not a frame identity
      const first = await this.wire.startOriginal(seed, this.options.mascotId ?? 0, key);
      this.sessionId = first.sessionId;
      this.seq = -1;
      this.deliver(first);
      await this.wire.startLoop(first.sessionId);
      this.keepalive = setInterval(() => this.keepaliveTick(), KEEPALIVE_MS);
      void this.poll();
    } catch (error) {
      if (!this.disposed) this.setConnection({ kind: "failed", reason: describeFailure(failureOf(error), "Could not start the run") });
    }
  }

  private deliver(live: LiveFrame): void {
    if (!this.arena) return;
    const at = this.now();
    this.lastGoodAt = at;
    this.anchor = { tick: live.world.tick, at, rate: live.tickRate };
    const frame = toPlayFrame(live, this.arena, at);
    for (const l of this.frameListeners) l(frame);
    if (live.status === "Complete") this.setConnection({ kind: "ended", reason: "Run complete (recorded by the test host)." });
    else if (live.status === "Failed") this.setConnection({ kind: "failed", reason: live.error ?? "The run failed on the host." });
    else if (live.status === "Abandoned") this.setConnection({ kind: "failed", reason: live.error ?? "The host stopped the run (no client)." });
    else this.setConnection({ kind: "live" });
  }

  private async poll(): Promise<void> {
    let failures = 0;
    while (!this.disposed && this.sessionId) {
      try {
        const live = await this.wire.frame(this.sessionId, this.abort.signal);
        if (this.disposed) return;
        failures = 0;
        this.deliver(live);
        if (live.status === "Complete" || live.status === "Failed" || live.status === "Abandoned") return;
      } catch (error) {
        if (this.disposed) return;
        const failure = failureOf(error);
        failures += 1;
        const status = statusOf(failure);
        if (status === 404 || status === 401) {
          return this.setConnection({ kind: "failed", reason: describeFailure(failure, "The run is no longer available") });
        }
        if (failures >= DEGRADED_AFTER_FAILURES) this.setConnection({ kind: "degraded", reason: describeFailure(failure, "Frames are not arriving"), sinceMs: this.lastGoodAt });
        await sleep(150);
      }
      await sleep(POLL_GAP_MS);
    }
  }

  // ---- input -----------------------------------------------------------------------------------------------------------------
  // One request in flight, in order. Sequence numbers are assigned at send time, so a quick Burst tap can never be reordered into a
  // "sequence too old" refusal. A pressed Burst is always sent as its own request, even if the key was released before it left.
  setIntent(intent: ControlIntent, reason: "change" | "neutral"): void {
    if (this.disposed || !this.sessionId) return;
    const wasBurst = this.current.requestBurst;
    this.current = intent;
    if (reason === "neutral") this.neutralPending = true;
    else if (intent.requestBurst && !wasBurst) {
      this.burstPress = true;
      this.burstRetry = { until: this.now() + BURST_RETRY_WINDOW_MS, count: 0 };
    }
    this.dirty = true;
    this.pump();
  }

  private keepaliveTick(): void {
    const c = this.current;
    if (this.inflight || this.dirty || this.disposed) return;
    if (c.moveX !== 0 || c.moveY !== 0 || c.requestBurst) {
      this.dirty = true;
      this.pump();
    }
  }

  private estimateNextTick(): number {
    const a = this.anchor;
    return a.tick + 1 + Math.floor(((this.now() - a.at) * a.rate) / 1000);
  }

  private pump(): void {
    if (this.inflight || this.disposed || !this.sessionId) return;
    if (this.neutralPending) {
      this.neutralPending = false;
      this.burstPress = false;
      this.dirty = false;
      this.inflight = true;
      const sentAt = this.now();
      const burst = false;
      this.wire.neutral(this.sessionId).then(
        (answer) => this.answered(answer, sentAt, burst, this.seq),
        (error) => this.failed(error),
      );
      return;
    }
    if (!this.dirty && !this.burstPress) return;
    const c = this.current;
    // A queued press goes out as requestBurst=true; the release (if the key is already up) follows as the next request.
    const sendBurst = this.burstPress ? true : c.requestBurst;
    const intent: ControlIntent = { moveX: c.moveX, moveY: c.moveY, requestBurst: sendBurst };
    this.burstPress = false;
    this.dirty = sendBurst !== c.requestBurst; // the release still has to be sent
    this.seq += 1;
    const seq = this.seq;
    const sentAt = this.now();
    this.inflight = true;
    this.wire.input(this.sessionId, seq, this.estimateNextTick(), intent).then(
      (answer) => this.answered(answer, sentAt, sendBurst, seq),
      (error) => this.failed(error),
    );
  }

  private answered(answer: { accepted: boolean; rejection: string | null; nextTick: number; lastSequence: number }, sentAt: number, burst: boolean, seq: number): void {
    this.inflight = false;
    const answeredAt = this.now();
    this.seq = Math.max(this.seq, answer.lastSequence);
    // Every answer carries the server's real next tick: re-anchor the estimate (the greybox does the same on a timing refusal).
    this.anchor = { tick: answer.nextTick - 1, at: answeredAt, rate: this.anchor.rate };
    const retry = !answer.accepted && burst && !!answer.rejection && TRANSIENT_REFUSALS.has(answer.rejection) && answeredAt <= this.burstRetry.until && this.burstRetry.count < BURST_RETRY_MAX;
    if (retry) {
      this.burstRetry = { ...this.burstRetry, count: this.burstRetry.count + 1 };
      this.burstPress = true;
    }
    const view: InputAnswerView = { seq, accepted: answer.accepted, rejection: answer.rejection, burstRequested: burst, willRetry: retry, sentAtMs: sentAt, answeredAtMs: answeredAt };
    for (const l of this.answerListeners) l(view);
    this.pump();
  }

  private failed(error: unknown): void {
    this.inflight = false;
    if (this.disposed) return;
    this.setConnection({ kind: "degraded", reason: describeFailure(failureOf(error), "Input could not be sent"), sinceMs: this.lastGoodAt });
    // Not retried blindly: the next key change or keepalive sends the CURRENT intent. A lost press is therefore visible (no Burst confirms).
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    clearInterval(this.keepalive);
    this.abort.abort();
    this.frameListeners.clear();
    this.connectionListeners.clear();
    this.answerListeners.clear();
  }
}

function describeFailure(failure: TransportFailure, fallback: string): string {
  const f = failure as { kind: string; status?: number; message?: string };
  if (f.kind === "offline") return `${fallback}: the server cannot be reached.`;
  if (f.status) return `${fallback} (HTTP ${f.status}).`;
  return f.message ? `${fallback}: ${f.message}` : `${fallback}.`;
}

function statusOf(failure: TransportFailure): number | null {
  const s = (failure as { status?: unknown }).status;
  return typeof s === "number" ? s : null;
}

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
