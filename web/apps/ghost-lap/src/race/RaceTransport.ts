// PRODUCTION PlayTransport for a Ghost Challenge or a Fork over /hubs/ghostlap-solo (Spec 13, task 13.11). One hub connection per race.
//
//   prepare()  PrepareChallenge / PrepareFork   the server verifies the original and holds the full ghost trace. NOTHING runs. Ready, or a typed failure.
//   begin()    StartChallenge(operationKey)     THE LIVE 30 HZ CLOCK STARTS WITH THIS REPLY. No animation can pause or rewind it.
//   finish()   FinishChallenge / FinishFork     idempotent by the durable key; also answers after a refresh or a lost reply
//
// There is no resume: a race whose connection drops is interrupted by the server and never becomes a child, so automatic reconnect is off.
// Input handling (sequence, tick estimate, held-Burst edge, bounded Burst resend, neutral on blur) is the Solo transport's, with the same constants:
// the fork's first accepted input is for tick forkTick + 1, so the tick estimate anchors on the fork tick, not on 0.
//
// The ghost is read from the server's own ChallengeFrame.ghost (a verified state of the saved original at the same absolute tick). It is a position
// for the scene: its events are never turned into living events, so a ghost pickup or Burst can never sound, score or collect.
import { createAuthenticatedHubConnection, HubInputSubject } from "@sands/contracts";
import type { ActorSlot, Charge, ControlIntent } from "../presentation/types.ts";
import { NEUTRAL } from "../play/keyboardIntent.ts";
import type { ConnectionState, InputAnswerView, PlayEvent, PlayEventKind, PlayFrame, PlayTransport } from "../play/playTypes.ts";
import { KEEPALIVE_MS, MIN_BURST_HOLD_MS } from "../solo/SoloTransport.ts";
import {
  ensureGuestSession, hubFailureText, SOLO_HUB_PATH, SoloHubError, type GhostEventWire, type SoloInputItemWire, type SoloInputRejectedWire, type SoloRunEndedWire,
} from "../solo/soloWire.ts";
import type { HistoryFrameWire } from "../history/historyWire.ts";
import {
  parseFinish, parsePrepared, parseRaceFrame, parseStarted, RACE_ROUND_TICKS, RACE_TICK_RATE, type RaceAsk, type RaceFinishWire, type RaceFrameWire, type RaceKind,
  type RacePreparedWire, type RaceStartedWire,
} from "./raceWire.ts";

const KNOWN: ReadonlySet<string> = new Set<PlayEventKind>(["CandleCollected", "OverlitStarted", "ContactDrain", "BurstManual", "BurstAuto", "BurstHit", "OverlitCancelled", "RoundFinished"]);
const BURST_RETRY_WINDOW_MS = 400;
const BURST_RETRY_MAX = 4;
const FIXED = 1024;

export interface RaceRun { readonly identity: string; readonly mascotId: number; readonly kind: RaceKind; readonly forkTick: number; readonly tickRateHz: number; readonly roundTicks: number }

/** The tick-shaped part of a started reply or a pushed frame. */
interface RaceTick { readonly tick: number; readonly living: RaceFrameWire["living"]; readonly ghost: RaceFrameWire["ghost"]; readonly delta: number; readonly final: boolean; readonly seq?: number; readonly missed?: RaceFrameWire["missed"]; readonly missedTruncated?: boolean }

/**
 * Living frame + ghost -> PlayFrame, from the delivered fields only. `slot` is the chosen mascot: the living head IS the original's head (13.0 map A3).
 * `withEvents` is false for the frame that comes with the Start reply: its events belong to the recorded history up to the fork tick, not to a living
 * decision, so they are neither felt nor heard again.
 */
export function racePlayFrame(run: RaceRun, t: RaceTick, receivedAtMs: number, canControl: boolean, withEvents: boolean, eventGap: boolean): PlayFrame {
  const slot = run.mascotId as ActorSlot;
  const f = t.living;
  const mapEvents = (tick: number, events: readonly GhostEventWire[]): PlayEvent[] => events.map((e, i) => ({
    key: `${run.identity}:${tick}:${i}`, tick, kind: (KNOWN.has(e.kind) ? e.kind : "Unknown") as PlayEventKind, raw: e.kind,
    slot: e.slot === 0 ? slot : e.slot, otherSlot: e.otherSlot === 0 ? slot : e.otherSlot, value: e.value, candleId: e.candleId,
  }));
  const events = withEvents ? [...(t.missed ?? []).flatMap((m) => mapEvents(m.tick, m.living)), ...mapEvents(t.tick, f.events ?? [])] : [];
  return {
    identity: run.identity, tick: t.tick, tickRate: run.tickRateHz, roundTicks: run.roundTicks, final: t.final, receivedAtMs,
    ...((t.seq ?? 0) > 0 ? { snapshotSequence: t.seq } : {}),
    players: [{
      slot, x: f.xFixed / FIXED, y: f.yFixed / FIXED, charge: f.charge as Charge,
      overlit: f.overlit === true || f.overlitDeadlineTick > 0, overlitDeadlineTick: f.overlitDeadlineTick > 0 ? f.overlitDeadlineTick : null,
      totalScore: f.totalScore, movementPoints: f.movementPoints ?? 0, pickupPoints: f.pickupPoints ?? 0, penaltyPoints: f.penaltyPoints ?? 0,
      pickups: f.pickups ?? 0, bursts: f.bursts ?? 0, appliedSeq: f.ackedInputSeq ?? null,
    }],
    candles: f.visibleCandles ?? [],
    ghost: { slot, x: t.ghost.xFixed / FIXED, y: t.ghost.yFixed / FIXED },
    race: { kind: run.kind, forkTick: run.forkTick, sourceScore: t.ghost.totalScore, sourceCharge: t.ghost.charge as Charge, delta: t.delta },
    events,
    you: { slot, canControl: canControl && !t.final },
    eventsComplete: !eventGap && (!withEvents || (f.events != null && t.missedTruncated !== true)),
  };
}

/** The recorded state of the ORIGINAL at the chosen tick as a frame for the night reveal: the living head and its ghost are the same recorded state. */
export function recordedPlayFrame(run: RaceRun, rec: HistoryFrameWire, receivedAtMs: number): PlayFrame {
  const slot = run.mascotId as ActorSlot;
  return {
    identity: run.identity, tick: rec.tick, tickRate: run.tickRateHz, roundTicks: run.roundTicks, final: false, receivedAtMs,
    players: [{
      slot, x: rec.xFixed / FIXED, y: rec.yFixed / FIXED, charge: rec.charge as Charge, overlit: rec.overlit, overlitDeadlineTick: rec.overlitDeadlineTick > 0 ? rec.overlitDeadlineTick : null,
      totalScore: rec.totalScore, movementPoints: rec.movementPoints, pickupPoints: rec.pickupPoints, penaltyPoints: rec.penaltyPoints, pickups: rec.pickups, bursts: rec.bursts, appliedSeq: null,
    }],
    candles: rec.visibleCandles ?? [],
    ghost: { slot, x: rec.xFixed / FIXED, y: rec.yFixed / FIXED },
    race: { kind: run.kind, forkTick: run.forkTick, sourceScore: rec.totalScore, sourceCharge: rec.charge as Charge, delta: 0 },
    events: [], you: { slot, canControl: false }, eventsComplete: true,
  };
}

export interface RaceTransportOptions {
  readonly kind: RaceKind; readonly originalId: string; readonly forkTick: number; readonly mascotId: number; readonly operationKey: string;
  readonly baseUrl?: string; readonly now?: () => number;
}

function hubFor(baseUrl: string) {
  return createAuthenticatedHubConnection({ baseUrl, hubPath: SOLO_HUB_PATH, keepAliveMs: 5000, serverTimeoutMs: 15000, reconnectDelaysMs: [] });
}
async function invokeReply<T>(hub: ReturnType<typeof hubFor>, method: string, ...args: unknown[]): Promise<T> {
  const reply = await hub.invoke<{ ok: boolean; code: string | null; value: T }>(method, ...args);
  if (!reply.ok) throw new SoloHubError(method, reply.code ?? "unknown");
  return reply.value;
}
const finishMethod = (kind: RaceKind) => (kind === "Fork" ? "FinishFork" : "FinishChallenge");

/**
 * The result of a finished race by its durable key, on a fresh short connection. A Fork is resolved with FinishFork and a Challenge with
 * FinishChallenge; the two are different operations and a key for one is never asked as the other.
 */
export async function finishRaceByKey(kind: RaceKind, operationKey: string, baseUrl = "", signal?: AbortSignal): Promise<RaceFinishWire> {
  await ensureGuestSession(signal);
  const hub = hubFor(baseUrl);
  try {
    await hub.start();
    const view = parseFinish(await invokeReply<unknown>(hub, finishMethod(kind), operationKey));
    if (!view) throw new SoloHubError(finishMethod(kind), "malformed");
    return view;
  } finally {
    await hub.stop().catch(() => undefined);
  }
}

export class RaceTransport implements PlayTransport {
  readonly label: string;
  readonly production = true;
  readonly ask: RaceAsk;
  readonly operationKey: string;
  prepared: RacePreparedWire | null = null;
  started: RaceStartedWire | null = null;
  ended: SoloRunEndedWire | null = null;
  /** "rich" once the recorded start or a living frame carries candle positions. */
  delivery: "basic" | "rich" = "basic";
  onStarted: (run: RaceStartedWire) => void = () => {};
  onFinalFrame: () => void = () => {};
  onEnded: (ended: SoloRunEndedWire) => void = () => {};
  /** The connection dropped or the server interrupted the race before the Start reply or after it. */
  onLost: (reason: string, started: boolean) => void = () => {};

  private readonly hub: ReturnType<typeof hubFor>;
  private readonly now: () => number;
  private readonly run: RaceRun;
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

  constructor(options: RaceTransportOptions) {
    this.ask = { kind: options.kind, originalId: options.originalId, forkTick: options.forkTick, mascotId: options.mascotId };
    this.operationKey = options.operationKey;
    this.label = `PRODUCTION HUB · ${options.kind === "Fork" ? `fork at T${options.forkTick}` : "ghost challenge"}`;
    this.run = { identity: options.operationKey, mascotId: options.mascotId, kind: options.kind, forkTick: options.forkTick, tickRateHz: RACE_TICK_RATE, roundTicks: RACE_ROUND_TICKS };
    this.now = options.now ?? (() => performance.now());
    this.hub = hubFor(options.baseUrl ?? "");
    this.hub.on("ChallengeFrame", (f: unknown) => this.receive(f));
    this.hub.on("InputRejected", (r: SoloInputRejectedWire) => this.rejected(r));
    this.hub.on("ChallengeEnded", (e: SoloRunEndedWire) => this.runEnded(e));
    this.hub.onclose(() => {
      this.hubOpen = false;
      if (this.disposed || this.closing || this.ended) return; // after ChallengeEnded the server completes on its own; a closed connection changes nothing
      if (this.finalFrame) this.runEnded({ reason: "final", code: null });
      else if (this.started) this.interrupt("The connection to the game server was lost. An unfinished race can't be resumed, and nothing was saved.", true);
      else this.interrupt("The connection to the game server was lost before the race started. Nothing was started.", false);
    });
  }

  onFrame(l: (f: PlayFrame) => void) { this.frames.add(l); return () => { this.frames.delete(l); }; }
  onConnection(l: (s: ConnectionState) => void) { this.connections.add(l); return () => { this.connections.delete(l); }; }
  onInputAnswer(l: (a: InputAnswerView) => void) { this.answers.add(l); return () => { this.answers.delete(l); }; }
  private state(s: ConnectionState) { for (const l of this.connections) l(s); }
  private emit(frame: PlayFrame) { for (const l of this.frames) l(frame); }

  /** PlayTransport.start: connects and prepares. A caller that needs the Ready reply calls `prepare` and awaits it. */
  async start(signal: AbortSignal): Promise<void> { await this.prepare(signal).then(() => undefined, () => undefined); }

  // ---- prepare / begin -----------------------------------------------------------------------------------------------------------------
  /** Connects and asks the server to prepare. Resolves only with a verified Ready reply for exactly the ask; throws a SoloHubError otherwise. */
  async prepare(signal: AbortSignal): Promise<RacePreparedWire> {
    if (this.disposed) throw new SoloHubError("PrepareChallenge", "unavailable");
    signal.addEventListener("abort", () => this.dispose(), { once: true });
    this.state({ kind: "connecting" });
    await ensureGuestSession(signal);
    if (this.disposed || signal.aborted) throw new SoloHubError("PrepareChallenge", "unavailable");
    if (!this.hubOpen) {
      try { await this.hub.start(); } catch { throw new Error("Could not open the game connection. Solo races may not be enabled on this server."); }
      this.hubOpen = true;
    }
    if (this.disposed) throw new SoloHubError("PrepareChallenge", "unavailable");
    const method = this.ask.kind === "Fork" ? "PrepareFork" : "PrepareChallenge";
    const reply = await invokeReply<unknown>(this.hub, method, this.ask.kind === "Fork" ? { originalId: this.ask.originalId, forkTick: this.ask.forkTick } : { originalId: this.ask.originalId });
    if (this.disposed) throw new SoloHubError(method, "unavailable");
    this.prepared = parsePrepared(reply, this.ask);
    return this.prepared;
  }

  /** Shows the recorded state at the chosen tick (living head and ghost on the same recorded state) for the reveal. Cannot be controlled. */
  showRecorded(rec: HistoryFrameWire): void {
    if (this.disposed) return;
    if (rec.visibleCandles != null) this.delivery = "rich";
    this.emit(recordedPlayFrame(this.run, rec, this.now()));
  }

  /**
   * StartChallenge. The server's live clock runs from THIS reply: the caller must be able to receive control and draw the first state already.
   * Resolves with the parsed reply (checked against the ask); the first authoritative frame is emitted to the session before this resolves.
   */
  async begin(): Promise<RaceStartedWire> {
    if (this.disposed || !this.prepared || this.started) throw new SoloHubError("StartChallenge", "not_ready");
    const reply = await invokeReply<unknown>(this.hub, "StartChallenge", { operationKey: this.operationKey });
    if (this.disposed) throw new SoloHubError("StartChallenge", "unavailable");
    // No await from here to the end of this block: the next server push is a separate task and must find the run already set up.
    const run = parseStarted(reply, this.ask, this.operationKey);
    this.started = run;
    this.locked = false;
    this.lastTick = run.forkTick;
    this.anchor = { tick: run.forkTick, at: this.now() };
    // A Fork restores the living player's last applied sequence along with the world at T.
    this.seq = this.lastAck = run.living.ackedInputSeq ?? -1;
    this.delivery = run.living.visibleCandles != null ? "rich" : "basic";
    const stream = new HubInputSubject<SoloInputItemWire>();
    this.stream = stream;
    void this.hub.invoke<{ ok: boolean; code: string | null }>("StreamChallengeInputs", stream)
      .then((r) => { if (!this.disposed && this.stream === stream && !this.ended && !r.ok) this.interrupt(`Input stream refused: ${hubFailureText(new SoloHubError("StreamChallengeInputs", r.code ?? "unknown"))}`, true); })
      .catch((e) => { if (!this.disposed && this.stream === stream && !this.ended) this.interrupt(hubFailureText(e), true); });
    this.keepalive = setInterval(() => this.keepaliveTick(), KEEPALIVE_MS);
    this.emit(racePlayFrame(this.run, { tick: run.forkTick, living: run.living, ghost: run.ghost, delta: run.living.totalScore - run.ghost.totalScore, final: false }, this.now(), true, false, false));
    this.state({ kind: "live" });
    this.onStarted(run);
    return run;
  }

  /** Discards an unfinished challenge on the server (never a result). Best effort: closing the connection does the same. */
  async cancel(): Promise<void> {
    if (!this.hubOpen || this.disposed || this.ended) return;
    try { await this.hub.invoke("CancelChallenge"); } catch { /* the connection closing discards it anyway */ }
  }

  private interrupt(reason: string, started: boolean) {
    this.locked = true; this.current = NEUTRAL; this.clearTimers();
    this.state({ kind: "failed", reason });
    this.onLost(reason, started);
  }
  private clearTimers() { clearInterval(this.keepalive); clearTimeout(this.release); this.keepalive = undefined; this.release = undefined; }
  private async stopHub() { this.hubOpen = false; await this.hub.stop().catch(() => undefined); }

  // ---- frames --------------------------------------------------------------------------------------------------------------------------
  private receive(raw: unknown) {
    const run = this.started;
    if (this.disposed || this.ended || this.finalFrame || !run) return;
    const f = parseRaceFrame(raw);
    if (!f) return; // not a frame this page understands: ignored, never guessed at
    const seq = f.seq ?? 0;
    if (f.tick <= this.lastTick || (seq > 0 && seq <= this.lastFrameSequence)) return;
    if (seq > 0) {
      this.eventGap ||= (this.lastFrameSequence > 0 && seq !== this.lastFrameSequence + 1) || f.missedTruncated === true;
      this.lastFrameSequence = seq;
    } else if (f.missedTruncated === true) this.eventGap = true;
    this.lastTick = f.tick;
    const at = this.now();
    this.anchor = { tick: f.tick, at };
    if (f.living.visibleCandles != null) this.delivery = "rich";
    this.lastAck = f.living.ackedInputSeq ?? this.lastAck;
    if (this.neutralSeq !== null && this.lastAck >= this.neutralSeq) this.neutralSeq = null;
    if (f.final) {
      this.finalFrame = true;
      this.locked = true;
      this.clearTimers();
      this.onFinalFrame();
    } else if (this.burstPress?.retryAfterTick !== undefined && f.tick >= this.burstPress.retryAfterTick) {
      this.retryBurst(at);
    }
    this.emit(racePlayFrame(this.run, f, at, !this.locked && !this.ended, true, this.eventGap));
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
    if (e.reason === "final") this.state({ kind: "ended", reason: `All ${RACE_ROUND_TICKS} ticks were committed.` });
    else this.state({ kind: "failed", reason: e.code ? `The server interrupted the race (${e.code}). It was not saved.` : "The server interrupted the race. It was not saved." });
  }

  // ---- input ---------------------------------------------------------------------------------------------------------------------------
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

  /** The tick the server will most likely stamp the next input with: one past the last committed tick, plus what has elapsed since that frame. */
  private estimateTick(): number { return this.anchor.tick + 1 + Math.floor(((this.now() - this.anchor.at) * RACE_TICK_RATE) / 1000); }

  private send(intent: ControlIntent) {
    if (this.disposed || this.locked || !this.stream) return;
    const seq = ++this.seq;
    const neutral = intent.moveX === 0 && intent.moveY === 0 && !intent.requestBurst;
    this.neutralSeq = neutral ? this.neutralSeq ?? seq : null;
    this.stream.next({ seq, clientTick: this.estimateTick(), moveX: intent.moveX, moveY: intent.moveY, requestBurst: intent.requestBurst });
  }

  /** Only refusals are reported (the stream sends no per-input ack); a refused Burst PRESS is re-sent a bounded number of times. */
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

  // ---- result ---------------------------------------------------------------------------------------------------------------------------
  /** The idempotent result of THIS race (FinishFork for a Fork, FinishChallenge for a Challenge). Uses this connection while open, else a fresh one. */
  async finish(): Promise<RaceFinishWire> {
    if (this.hubOpen && !this.disposed) {
      const view = parseFinish(await invokeReply<unknown>(this.hub, finishMethod(this.ask.kind), this.operationKey));
      if (!view) throw new SoloHubError(finishMethod(this.ask.kind), "malformed");
      return view;
    }
    return finishRaceByKey(this.ask.kind, this.operationKey);
  }

  dispose(): void {
    if (this.disposed) return;
    if (this.stream && !this.locked) this.send(NEUTRAL);
    this.disposed = true;
    this.closing = true;
    this.clearTimers();
    this.stream?.complete();
    this.stream = null;
    this.frames.clear(); this.connections.clear(); this.answers.clear();
    void this.stopHub();
  }
}
