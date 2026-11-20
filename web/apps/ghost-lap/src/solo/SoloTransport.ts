// PRODUCTION PlayTransport for an ORIGINAL solo run over /hubs/ghostlap-solo (Spec 13, task 13.9). One hub connection per run.
//
//   StartRun(StartSoloRequest) -> SoloRunStarted   the server seals the seed, starts the 30 Hz authoritative loop and ADVANCES IMMEDIATELY
//   StreamInputs(SoloInputItem)                    one client-to-server stream for the whole run
//   Frame(SoloFrame) / RunEnded / InputRejected    server pushes, latest-wins
//   FinishRun(operationKey) -> SoloSaveView        idempotent, durable: also answers after a refresh or a lost reply
//
// There is no resume: an unfinished run whose connection drops is interrupted by the server and never becomes an original, so this transport
// disables SignalR's automatic reconnect (a reconnected connection would own no run).
import { createAuthenticatedHubConnection, HubInputSubject } from "@sands/contracts";
import type { ActorSlot, Charge, ControlIntent } from "../presentation/types.ts";
import { NEUTRAL } from "../play/keyboardIntent.ts";
import type { ConnectionState, InputAnswerView, PlayEvent, PlayEventKind, PlayFrame, PlayTransport } from "../play/playTypes.ts";
import {
  ensureGuestSession, hubFailureText, isRunStarted, isSave, SOLO_HUB_PATH, SoloHubError,
  type GhostEventWire, type SoloFrameWire, type SoloInputItemWire, type SoloInputRejectedWire, type SoloRunEndedWire, type SoloRunStartedWire, type SoloSaveWire,
} from "./soloWire.ts";

const KNOWN: ReadonlySet<string> = new Set<PlayEventKind>(["CandleCollected", "OverlitStarted", "ContactDrain", "BurstManual", "BurstAuto", "BurstHit", "OverlitCancelled", "RoundFinished"]);
/** ghost-v1 holds a vector for at most six ticks (200 ms) without newer input, so a held key re-sends inside that window. */
export const KEEPALIVE_MS = 90;
/** A pressed Burst is held for at least this long before its release is sent, so press and release can never land in one server tick. */
export const MIN_BURST_HOLD_MS = 50;
const BURST_RETRY_WINDOW_MS = 400;
const BURST_RETRY_MAX = 4;
const PINNED = { rulesVersion: "ghost-v1", layoutVersion: "arena-v1", tickRateHz: 30, roundTicks: 2700 } as const;

/**
 * SoloFrame -> PlayFrame, from the delivered fields only. `slot` is the chosen mascot: a solo run's head IS its MascotId (0-3), which is
 * not necessarily the P1 head (13.0 map A3).
 */
export function soloPlayFrame(run: Pick<SoloRunStartedWire, "runId" | "mascotId" | "tickRateHz" | "roundTicks">, f: SoloFrameWire, receivedAtMs: number, canControl: boolean): PlayFrame {
  const slot = run.mascotId as ActorSlot;
  const mapEvents = (tick: number, events: GhostEventWire[]): PlayEvent[] => events.map((e, i) => ({
    key: `${run.runId}:${tick}:${i}`, tick, kind: (KNOWN.has(e.kind) ? e.kind : "Unknown") as PlayEventKind, raw: e.kind,
    slot: e.slot === 0 ? slot : e.slot, otherSlot: e.otherSlot === 0 ? slot : e.otherSlot, value: e.value, candleId: e.candleId,
  }));
  const delivered = f.events != null;
  return {
    identity: run.runId, tick: f.tick, tickRate: run.tickRateHz, roundTicks: run.roundTicks, final: f.final, receivedAtMs,
    ...((f.seq ?? 0) > 0 ? { snapshotSequence: f.seq } : {}),
    players: [{
      slot, x: f.xFixed / 1024, y: f.yFixed / 1024, charge: f.charge as Charge,
      overlit: delivered ? f.overlit ?? f.overlitDeadlineTick > 0 : f.overlitDeadlineTick > 0,
      overlitDeadlineTick: f.overlitDeadlineTick > 0 ? f.overlitDeadlineTick : null,
      totalScore: f.totalScore,
      movementPoints: delivered ? f.movementPoints ?? 0 : 0, pickupPoints: delivered ? f.pickupPoints ?? 0 : 0, penaltyPoints: delivered ? f.penaltyPoints ?? 0 : 0,
      pickups: delivered ? f.pickups ?? 0 : 0, bursts: delivered ? f.bursts ?? 0 : 0,
      appliedSeq: delivered ? f.ackedInputSeq ?? null : null,
    }],
    candles: f.visibleCandles ?? [],
    events: [...(f.missed ?? []).flatMap((m) => mapEvents(m.tick, m.events)), ...mapEvents(f.tick, f.events ?? [])],
    you: { slot, canControl: canControl && !f.final },
    eventsComplete: delivered && f.missed != null && !f.missedTruncated,
  };
}

export interface SoloTransportOptions { readonly mascotId: number; readonly operationKey: string; readonly baseUrl?: string; readonly now?: () => number }

function hubFor(baseUrl: string) {
  return createAuthenticatedHubConnection({ baseUrl, hubPath: SOLO_HUB_PATH, keepAliveMs: 5000, serverTimeoutMs: 15000, reconnectDelaysMs: [] });
}
async function invokeReply<T>(hub: ReturnType<typeof hubFor>, method: string, ...args: unknown[]): Promise<T> {
  const reply = await hub.invoke<{ ok: boolean; code: string | null; value: T }>(method, ...args);
  if (!reply.ok) throw new SoloHubError(method, reply.code ?? "unknown");
  return reply.value;
}

/**
 * Asks the server for the saved result of a finished run by its durable key on a fresh short connection. This is how a save is recovered
 * after a refresh or a closed connection (the server saves a finished run whether or not a client is still attached).
 */
export async function finishRunByKey(operationKey: string, baseUrl = "", signal?: AbortSignal): Promise<SoloSaveWire> {
  await ensureGuestSession(signal);
  const hub = hubFor(baseUrl);
  try {
    await hub.start();
    const view = await invokeReply<SoloSaveWire>(hub, "FinishRun", operationKey);
    if (!isSave(view)) throw new SoloHubError("FinishRun", "malformed");
    return { ...view, original: view.original ?? null, code: view.code ?? null };
  } finally {
    await hub.stop().catch(() => undefined);
  }
}

export class SoloTransport implements PlayTransport {
  readonly label = "PRODUCTION HUB · solo original";
  readonly production = true;
  readonly mascotId: number;
  readonly operationKey: string;
  started: SoloRunStartedWire | null = null;
  ended: SoloRunEndedWire | null = null;
  delivery: "basic" | "rich" = "basic";
  onStarted: (run: SoloRunStartedWire) => void = () => {};
  onFinalFrame: () => void = () => {};
  onEnded: (ended: SoloRunEndedWire) => void = () => {};

  private readonly hub: ReturnType<typeof hubFor>;
  private readonly now: () => number;
  private frames = new Set<(f: PlayFrame) => void>();
  private connections = new Set<(s: ConnectionState) => void>();
  private answers = new Set<(a: InputAnswerView) => void>();
  private stream: HubInputSubject<SoloInputItemWire> | null = null;
  private current: ControlIntent = NEUTRAL;
  private lastTick = -1;
  private lastFrameSequence = 0;
  private eventGap = false;
  private finalFrame = false;
  private lastAck = -1;
  private neutralSeq: number | null = null;
  private anchor = { tick: 0, at: 0 };
  private seq = -1;
  private locked = true;
  private disposed = false;
  private closing = false;
  private hubOpen = false;
  private keepalive: ReturnType<typeof setInterval> | undefined;
  private release: ReturnType<typeof setTimeout> | undefined;
  private burstHoldUntil = 0;
  private burstPress: { until: number; retries: number; retryAfterTick?: number } | null = null;

  constructor(options: SoloTransportOptions) {
    this.mascotId = options.mascotId;
    this.operationKey = options.operationKey;
    this.now = options.now ?? (() => performance.now());
    this.hub = hubFor(options.baseUrl ?? "");
    this.hub.on("Frame", (f: SoloFrameWire) => this.receive(f));
    this.hub.on("InputRejected", (r: SoloInputRejectedWire) => this.rejected(r));
    this.hub.on("RunEnded", (e: SoloRunEndedWire) => this.runEnded(e));
    this.hub.onclose(() => {
      this.hubOpen = false;
      if (this.disposed || this.closing || this.ended) return; // after RunEnded the server saves on its own; a closed connection changes nothing
      if (this.finalFrame) this.runEnded({ reason: "final", code: null });
      else this.interrupt("The connection to the game server was lost. An unfinished run can't be resumed. Check My Histories for the save status.");
    });
  }

  onFrame(l: (f: PlayFrame) => void) { this.frames.add(l); return () => { this.frames.delete(l); }; }
  onConnection(l: (s: ConnectionState) => void) { this.connections.add(l); return () => { this.connections.delete(l); }; }
  onInputAnswer(l: (a: InputAnswerView) => void) { this.answers.add(l); return () => { this.answers.delete(l); }; }
  private state(s: ConnectionState) { for (const l of this.connections) l(s); }

  async start(signal: AbortSignal): Promise<void> {
    signal.addEventListener("abort", () => this.dispose(), { once: true });
    this.state({ kind: "connecting" });
    try {
      await ensureGuestSession(signal);
      if (this.disposed || signal.aborted) return;
      try { await this.hub.start(); } catch { throw new Error("Could not open the solo connection. Solo runs may not be enabled on this server."); }
      this.hubOpen = true;
      if (this.disposed) { await this.stopHub(); return; }
      const run = await invokeReply<SoloRunStartedWire>(this.hub, "StartRun", { operationKey: this.operationKey, mascotId: this.mascotId });
      if (this.disposed) return;
      if (!isRunStarted(run)) throw new Error("The server's run description was not recognised.");
      if (run.rulesVersion !== PINNED.rulesVersion || run.layoutVersion !== PINNED.layoutVersion || run.tickRateHz !== PINNED.tickRateHz || run.roundTicks !== PINNED.roundTicks || run.mascotId !== this.mascotId) {
        throw new Error("The server started a run under rules or a layout this page does not draw. Refusing to play.");
      }
      this.started = run;
      this.locked = false;
      this.anchor = { tick: 0, at: this.now() };
      // The run is already advancing. Open the input stream at once; the first ticks are simply unplayed.
      const stream = new HubInputSubject<SoloInputItemWire>();
      this.stream = stream;
      void this.hub.invoke<{ ok: boolean; code: string | null }>("StreamInputs", stream)
        .then((reply) => { if (!this.disposed && this.stream === stream && !this.ended && !reply.ok) this.interrupt(`Input stream refused: ${hubFailureText(new SoloHubError("StreamInputs", reply.code ?? "unknown"))}`); })
        .catch((e) => { if (!this.disposed && this.stream === stream && !this.ended) this.interrupt(hubFailureText(e)); });
      this.keepalive = setInterval(() => this.keepaliveTick(), KEEPALIVE_MS);
      this.onStarted(run);
      this.state({ kind: "live" });
    } catch (e) {
      if (this.disposed) return;
      this.closing = true;
      this.fail(hubFailureText(e));
      await this.stopHub();
    }
  }

  private fail(reason: string) { this.locked = true; this.current = NEUTRAL; this.clearTimers(); this.state({ kind: "failed", reason }); }
  /** The run is gone (connection lost or server-side interruption): input stops and the screen reports an unsaved, non-resumable run. */
  private interrupt(reason: string) { this.fail(reason); }
  private clearTimers() { clearInterval(this.keepalive); clearTimeout(this.release); this.keepalive = undefined; this.release = undefined; }
  private async stopHub() { this.hubOpen = false; await this.hub.stop().catch(() => undefined); }

  private receive(f: SoloFrameWire) {
    const run = this.started;
    const seq = f.seq ?? 0;
    if (this.disposed || this.ended || this.finalFrame || !run || f.tick <= this.lastTick || (seq > 0 && seq <= this.lastFrameSequence)) return;
    if (seq > 0) {
      this.eventGap ||= seq !== this.lastFrameSequence + 1 || f.missedTruncated === true;
      this.lastFrameSequence = seq;
    }
    this.lastTick = f.tick;
    const at = this.now();
    this.anchor = { tick: f.tick, at };
    if (f.visibleCandles != null) this.delivery = "rich";
    this.lastAck = f.ackedInputSeq ?? this.lastAck;
    if (this.neutralSeq !== null && this.lastAck >= this.neutralSeq) this.neutralSeq = null;
    if (f.final) {
      this.finalFrame = true;
      this.locked = true;
      this.clearTimers();
      this.onFinalFrame();
    } else if (this.burstPress?.retryAfterTick !== undefined && f.tick >= this.burstPress.retryAfterTick) {
      this.retryBurst(at);
    }
    const frame = soloPlayFrame(run, f, at, !this.locked && !this.ended);
    const delivered = this.eventGap ? { ...frame, eventsComplete: false } : frame;
    for (const l of this.frames) l(delivered);
  }

  private runEnded(e: SoloRunEndedWire) {
    if (this.disposed || this.ended) return;
    this.ended = e;
    this.locked = true;
    this.current = NEUTRAL;
    this.clearTimers();
    this.stream?.complete();
    this.stream = null;
    this.onEnded(e);
    if (e.reason === "final") this.state({ kind: "ended", reason: "All 2,700 ticks were committed." });
    else this.state({ kind: "failed", reason: e.code ? `The server interrupted the run (${e.code}). It was not saved.` : "The server interrupted the run. It was not saved." });
  }

  // ---- input -------------------------------------------------------------------------------------------------------------------------
  setIntent(intent: ControlIntent, reason: "change" | "neutral"): void {
    if (this.disposed || !this.stream || this.locked) return;
    const prev = this.current;
    this.current = intent;
    const t = this.now();
    if (reason === "neutral") { this.burstPress = null; clearTimeout(this.release); this.release = undefined; this.send(intent); return; }
    if (intent.requestBurst && !prev.requestBurst) {
      this.burstPress = { until: t + BURST_RETRY_WINDOW_MS, retries: 0 };
      this.burstHoldUntil = t + MIN_BURST_HOLD_MS;
      this.send(intent);
      return;
    }
    if (!intent.requestBurst && t < this.burstHoldUntil) { this.scheduleRelease(); return; }
    clearTimeout(this.release); this.release = undefined;
    this.send(intent);
  }

  private scheduleRelease() {
    clearTimeout(this.release);
    this.release = setTimeout(() => { this.release = undefined; if (!this.disposed && !this.locked) this.send(this.current); }, Math.max(0, this.burstHoldUntil - this.now()));
  }

  private keepaliveTick() {
    const c = this.current;
    if (this.release === undefined && (c.moveX !== 0 || c.moveY !== 0 || c.requestBurst || this.neutralSeq !== null)) this.send(c);
  }

  private estimateTick(): number { return this.anchor.tick + 1 + Math.floor(((this.now() - this.anchor.at) * 30) / 1000); }

  private send(intent: ControlIntent) {
    if (this.disposed || this.locked || !this.stream) return;
    const seq = ++this.seq;
    const neutral = intent.moveX === 0 && intent.moveY === 0 && !intent.requestBurst;
    this.neutralSeq = neutral ? this.neutralSeq ?? seq : null;
    this.stream.next({ seq, clientTick: this.estimateTick(), moveX: intent.moveX, moveY: intent.moveY, requestBurst: intent.requestBurst });
  }

  /** The server refuses with its own class. Only refusals are reported (the solo stream sends no per-input ack), and a refused Burst PRESS is re-sent a bounded number of times. */
  private rejected(r: SoloInputRejectedWire) {
    if (this.disposed || this.locked) return;
    const t = this.now();
    if (r.nextServerTick > this.lastTick) this.anchor = { tick: r.nextServerTick - 1, at: t };
    const press = this.burstPress;
    const retry = r.burstRequested && r.class === "burst_rejected" && r.seq > this.lastAck && !!press && t <= press.until && press.retries < BURST_RETRY_MAX;
    if (retry && press) {
      if (r.code === "CommitPending" && r.nextServerTick > this.lastTick) press.retryAfterTick = r.nextServerTick;
      else this.retryBurst(t);
    }
    const view: InputAnswerView = { seq: r.seq, accepted: false, rejection: r.code, burstRequested: r.burstRequested, willRetry: retry, sentAtMs: t, answeredAtMs: t };
    for (const l of this.answers) l(view);
  }

  private retryBurst(now: number) {
    const press = this.burstPress;
    if (!press || now > press.until) { this.burstPress = null; return; }
    this.burstPress = { until: press.until, retries: press.retries + 1 };
    this.burstHoldUntil = now + MIN_BURST_HOLD_MS;
    this.send({ ...this.current, requestBurst: true });
    if (!this.current.requestBurst) this.scheduleRelease();
  }

  // ---- save --------------------------------------------------------------------------------------------------------------------------
  /** The idempotent save status of THIS run. Uses the run's own connection while open; otherwise a fresh one (the server saves either way). */
  async finishRun(): Promise<SoloSaveWire> {
    if (this.hubOpen && !this.disposed) {
      const view = await invokeReply<SoloSaveWire>(this.hub, "FinishRun", this.operationKey);
      if (!isSave(view)) throw new SoloHubError("FinishRun", "malformed");
      return { ...view, original: view.original ?? null, code: view.code ?? null };
    }
    return finishRunByKey(this.operationKey);
  }

  dispose(): void {
    if (this.disposed) return;
    if (this.stream && !this.locked) this.send(NEUTRAL);
    this.disposed = true;
    this.clearTimers();
    this.stream?.complete();
    this.stream = null;
    this.frames.clear(); this.connections.clear(); this.answers.clear();
    void this.stopHub();
  }
}
