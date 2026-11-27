// One day-play session: takes authoritative PlayFrames from a transport, decides what is drawn where, and keeps the HUD honest.
// No Pixi and no React in here, so the rules are testable. It owns NO game rule: score, charge, pickups, Burst and collisions are only
// ever read from frames. What it adds is presentation: ordering, de-duplication, smoothing, and the admitted-vs-confirmed feedback.
//
//   frame    -> accept only newer ticks of the current identity (older = stale, other identity = reset), keep latest, queue new events
//   render() -> smoothed draw positions per slot (RemoteTrack for others, LocalSmoother for the controlled slot), connection watchdog
//   intent   -> forwarded to the transport unchanged; a Burst press opens a feedback line that only the server's own event can confirm
import type { ActorSlot, ControlIntent } from "../presentation/types.ts";
import type { ConnectionState, InputAnswerView, PlayEvent, PlayFrame, PlayTransport, PlayerFrame } from "./playTypes.ts";
import { LOCAL_PRESETS, LocalSmoother, RemoteTrack, ServerClock, type LocalPresetId, type Vec } from "./visualSmoothing.ts";

/** No frame for this long while the run is live: the HUD says so. Local animation is never allowed to hide it. */
export const STALL_MS = 700;
/** PROVISIONAL (13.4, human review pending): how long the 4/4 release face shows after the authoritative Burst cue. The server defines no length. */
export const RELEASE_VISUAL_MS = 450;
/** A requested Burst that the server never confirmed within this window is reported as not confirmed (charge < 4, refused, or lost). */
export const BURST_CONFIRM_WINDOW_MS = 1200;
const SEEN_KEYS = 256;
const FEED_LENGTH = 5;

export type BurstStage = "requested" | "admitted" | "confirmed" | "not_confirmed" | "refused";
export interface BurstFeedback { readonly id: number; readonly stage: BurstStage; readonly detail: string; readonly atMs: number }
export interface FeedLine { readonly key: string; readonly tick: number; readonly text: string; readonly tone: "gain" | "burst" | "loss" | "info" }

/** Where and how a confirmed Burst happened. Only the server's own BurstManual / BurstAuto event creates one; a key press never does. */
export interface BurstCue {
  /** "manual" = the player's own admitted press; "auto" = the Overlit deadline fired it. The server effect (reach, hits) is identical. */
  readonly origin: "manual" | "auto";
  /** Authoritative position of the emitting player in the frame that delivered the event, world units. Never the smoothed drawn position. */
  readonly x: number;
  readonly y: number;
  readonly tick: number;
}
export interface VisualCue {
  readonly kind: "fear" | "hit" | "release";
  readonly slot: number;
  readonly key: string;
  readonly untilMs?: number;
  /** Present on release cues whose emitter position is known. */
  readonly burst?: BurstCue;
}

export interface PlayMetrics {
  /** Round trip of the newest answered input, ms. null until one was answered. */
  readonly inputRttMs: number | null;
  /** Key press from rest -> first AUTHORITATIVE frame in which the local head moved, ms. */
  readonly moveToAuthoritativeMs: number | null;
  /** Key press from rest -> first drawn frame in which the local head moved, ms. */
  readonly moveToDrawnMs: number | null;
  /** Largest gap between delivered frames over the last 90 frames, ms (the feed's real jitter). */
  readonly worstFrameGapMs: number;
  readonly meanFrameGapMs: number;
  /** World units between the drawn local head and its newest authoritative position. */
  readonly visualLagUnits: number;
  readonly rejectedInputs: number;
  /** Count of refusals by the server's own reason, e.g. { commit_pending: 3 }. */
  readonly rejectionReasons: Readonly<Record<string, number>>;
}

export interface PlayHud {
  readonly transportLabel: string;
  readonly production: boolean;
  readonly connection: ConnectionState;
  readonly stalled: boolean;
  readonly identity: string | null;
  readonly tick: number;
  readonly tickRate: number;
  readonly roundTicks: number;
  readonly secondsLeft: number | null;
  readonly final: boolean;
  readonly players: readonly PlayerFrame[];
  readonly you: { readonly slot: ActorSlot | -1; readonly canControl: boolean } | null;
  readonly burst: BurstFeedback | null;
  readonly feed: readonly FeedLine[];
  readonly eventsComplete: boolean;
  readonly metrics: PlayMetrics;
  readonly preset: LocalPresetId;
}

const NO_METRICS: PlayMetrics = { inputRttMs: null, moveToAuthoritativeMs: null, moveToDrawnMs: null, worstFrameGapMs: 0, meanFrameGapMs: 0, visualLagUnits: 0, rejectedInputs: 0, rejectionReasons: {} };

export class PlaySession {
  private latest: PlayFrame | null = null;
  private connection: ConnectionState = { kind: "connecting" };
  private readonly clock = new ServerClock();
  private readonly remotes = new Map<number, RemoteTrack>();
  private readonly local = new LocalSmoother(LOCAL_PRESETS.light.tauMs);
  private preset: LocalPresetId = "light";
  private readonly seen = new Set<string>();
  private readonly seenOrder: string[] = [];
  private feed: FeedLine[] = [];
  private cues: VisualCue[] = [];
  private burst: BurstFeedback | null = null;
  private burstId = 0;
  private listeners = new Set<() => void>();
  private readonly eventListeners = new Set<(event: PlayEvent, frame: PlayFrame) => void>();
  private lastNotifyMs = 0;
  private dirty = true;
  private gaps: number[] = [];
  private lastArrivalMs: number | null = null;
  private metrics: PlayMetrics = NO_METRICS;
  private rejected = 0;
  private lastIntent: ControlIntent = { moveX: 0, moveY: 0, requestBurst: false };
  private pendingMove: { t0: number; x: number; y: number; authDone: boolean } | null = null;
  private drawn = new Map<number, Vec>();
  private readonly off: (() => void)[] = [];
  private disposed = false;

  readonly transport: PlayTransport;

  constructor(transport: PlayTransport) {
    this.transport = transport;
    this.off.push(transport.onFrame((f) => this.accept(f)));
    this.off.push(transport.onConnection((c) => { this.connection = c; this.dirty = true; }));
    this.off.push(transport.onInputAnswer((a) => this.answer(a)));
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /**
   * Hears every authoritative event exactly once per key (the same de-duplication the feed and the cues use), with the frame that delivered it.
   * A read-only tap for presentation that is not drawn (13.7 sound). A listener that throws cannot disturb play.
   */
  onEvent(listener: (event: PlayEvent, frame: PlayFrame) => void): () => void {
    this.eventListeners.add(listener);
    return () => { this.eventListeners.delete(listener); };
  }

  setPreset(id: LocalPresetId): void {
    this.preset = id;
    this.local.setTau(LOCAL_PRESETS[id].tauMs);
    this.dirty = true;
  }

  /** The intent the keyboard currently holds (for pupil gaze). */
  get held(): ControlIntent { return this.lastIntent; }

  /** The newest accepted authoritative frame (the scene reads charge, gaze and candles from it). */
  get frame(): PlayFrame | null { return this.latest; }
  /** Time constant of the local head's visual ease. The historical ghost uses the same one, so an exact overlap is drawn exactly. */
  get localTauMs(): number { return LOCAL_PRESETS[this.preset].tauMs; }

  /** Cues the scene should apply once. De-duplicated by event key, so a repeated frame never replays them. */
  drainCues(): VisualCue[] {
    const out = this.cues;
    this.cues = [];
    return out;
  }

  // ---- input -----------------------------------------------------------------------------------------------------------------
  /** The keyboard changed. A pressed Burst opens an "requested" line; only the server's BurstManual event can turn it into "confirmed". */
  intent(next: ControlIntent, reason: "change" | "neutral", nowMs: number): void {
    if (this.disposed) return;
    const you = this.latest?.you;
    if (you && !you.canControl && reason !== "neutral") return; // spectators, superseded tabs and forfeited slots send nothing
    const previous = this.lastIntent;
    this.lastIntent = next;
    this.transport.setIntent(next, reason);
    if (next.requestBurst && !previous.requestBurst) {
      this.burst = { id: ++this.burstId, stage: "requested", detail: "Burst requested", atMs: nowMs };
    }
    const wasRest = previous.moveX === 0 && previous.moveY === 0;
    const moves = next.moveX !== 0 || next.moveY !== 0;
    if (wasRest && moves && this.latest && you) {
      const p = this.latest.players.find((q) => q.slot === you.slot);
      if (p) this.pendingMove = { t0: nowMs, x: p.x, y: p.y, authDone: false };
    }
    this.dirty = true;
  }

  private answer(a: InputAnswerView): void {
    this.metrics = { ...this.metrics, inputRttMs: a.answeredAtMs - a.sentAtMs, rejectedInputs: this.rejected };
    if (!a.accepted) {
      this.rejected += 1;
      const reason = a.rejection ?? "unspecified";
      this.metrics = { ...this.metrics, rejectedInputs: this.rejected, rejectionReasons: { ...this.metrics.rejectionReasons, [reason]: (this.metrics.rejectionReasons[reason] ?? 0) + 1 } };
    }
    if (a.burstRequested && this.burst && (this.burst.stage === "requested" || this.burst.stage === "admitted" || this.burst.stage === "refused")) {
      this.burst = a.accepted
        ? { ...this.burst, stage: "admitted", detail: "Burst admitted by the server; waiting for it to resolve" }
        : a.willRetry
          ? { ...this.burst, stage: "requested", detail: `Burst re-sent (server said ${a.rejection ?? "retry"})` }
          : { ...this.burst, stage: "refused", detail: `Burst refused by the server (${a.rejection ?? "rejected"})` };
    }
    this.dirty = true;
  }

  // ---- frames ----------------------------------------------------------------------------------------------------------------
  private accept(frame: PlayFrame): void {
    if (this.disposed) return;
    const prev = this.latest;
    if (prev && frame.identity !== prev.identity) this.resetForNewIdentity();
    else if (prev && frame.tick < prev.tick) return; // stale delivery
    else if (prev && frame.snapshotSequence !== undefined && prev.snapshotSequence !== undefined && frame.snapshotSequence < prev.snapshotSequence) return;
    else if (prev && frame.tick === prev.tick && frame.final === prev.final && frame.you.canControl === prev.you.canControl && frame.you.slot === prev.you.slot && frame.events.every(e => this.seen.has(e.key))) return; // duplicate
    this.noteArrival(frame.receivedAtMs);
    this.clock.observe((frame.tick * 1000) / frame.tickRate, frame.receivedAtMs);
    const you = frame.you;
    for (const p of frame.players) {
      if (p.slot === you.slot) this.local.setTarget(p.x, p.y);
      else {
        let track = this.remotes.get(p.slot);
        if (!track) this.remotes.set(p.slot, (track = new RemoteTrack(this.clock)));
        track.push(frame.tick, frame.tickRate, p.x, p.y);
      }
    }
    for (const e of frame.events) this.takeEvent(e, frame);
    const self = frame.players.find((p) => p.slot === you.slot);
    if (self && this.pendingMove && !this.pendingMove.authDone && Math.hypot(self.x - this.pendingMove.x, self.y - this.pendingMove.y) > 0.01) {
      this.pendingMove.authDone = true;
      this.metrics = { ...this.metrics, moveToAuthoritativeMs: frame.receivedAtMs - this.pendingMove.t0 };
    }
    if (!you.canControl && (this.lastIntent.moveX !== 0 || this.lastIntent.moveY !== 0 || this.lastIntent.requestBurst)) {
      this.lastIntent = { moveX: 0, moveY: 0, requestBurst: false };
      this.transport.setIntent(this.lastIntent, "neutral");
    }
    this.latest = frame;
    this.dirty = true;
  }

  /**
   * Drops everything of the run that just ended (frame, remembered event keys, feed, cues, Burst line). A multiplayer rematch returns the room
   * to its lobby: the scene sees no frame and clears itself, and the next match starts from nothing.
   */
  clear(): void {
    if (this.disposed) return;
    this.resetForNewIdentity();
    this.dirty = true;
  }

  private resetForNewIdentity(): void {
    this.remotes.clear();
    this.local.reset();
    this.clock.reset();
    this.seen.clear();
    this.seenOrder.length = 0;
    this.feed = [];
    this.cues = [];
    this.burst = null;
    this.gaps = [];
    this.lastArrivalMs = null;
    this.metrics = NO_METRICS;
    this.rejected = 0;
    this.pendingMove = null;
    this.drawn.clear();
    this.latest = null;
  }

  private noteArrival(atMs: number): void {
    if (this.lastArrivalMs !== null) {
      this.gaps.push(atMs - this.lastArrivalMs);
      if (this.gaps.length > 90) this.gaps.shift();
      const worst = Math.max(...this.gaps);
      const mean = this.gaps.reduce((s, g) => s + g, 0) / this.gaps.length;
      this.metrics = { ...this.metrics, worstFrameGapMs: worst, meanFrameGapMs: mean };
    }
    this.lastArrivalMs = atMs;
  }

  private takeEvent(e: PlayEvent, frame: PlayFrame): void {
    if (this.seen.has(e.key)) return;
    this.seen.add(e.key);
    this.seenOrder.push(e.key);
    if (this.seenOrder.length > SEEN_KEYS) this.seen.delete(this.seenOrder.shift()!);
    for (const listener of this.eventListeners) {
      try { listener(e, frame); } catch (error) { console.warn("play event listener failed", error); }
    }
    const mine = e.slot === frame.you.slot;
    const who = `P${e.slot + 1}`;
    switch (e.kind) {
      case "CandleCollected":
        this.pushFeed({ key: e.key, tick: e.tick, text: `${who} candle +${e.value}`, tone: "gain" });
        break;
      case "OverlitStarted":
        this.pushFeed({ key: e.key, tick: e.tick, text: `${who} Overlit: Burst before tick ${e.value}`, tone: "info" });
        break;
      case "BurstManual":
      case "BurstAuto": {
        const manual = e.kind === "BurstManual";
        const emitter = frame.players.find((p) => p.slot === e.slot);
        this.cues.push({ kind: "release", slot: e.slot, key: e.key, untilMs: RELEASE_VISUAL_MS, ...(emitter ? { burst: { origin: manual ? "manual" : "auto", x: emitter.x, y: emitter.y, tick: e.tick } satisfies BurstCue } : {}) });
        this.pushFeed({ key: e.key, tick: e.tick, text: `${who} ${manual ? "Burst" : "Overlit auto-Burst"} hit ${e.value}`, tone: "burst" });
        if (mine && manual && this.burst && this.burst.stage !== "confirmed") {
          this.burst = { ...this.burst, stage: "confirmed", detail: `Burst confirmed at tick ${e.tick}, ${e.value} hit`, atMs: frame.receivedAtMs };
        }
        break;
      }
      case "BurstHit":
        this.cues.push({ kind: "hit", slot: e.slot, key: e.key });
        this.pushFeed({ key: e.key, tick: e.tick, text: `${who} hit by P${e.otherSlot + 1}'s Burst, -${e.value}`, tone: "loss" });
        break;
      case "ContactDrain":
        this.cues.push({ kind: "fear", slot: e.slot, key: e.key });
        this.pushFeed({ key: e.key, tick: e.tick, text: `${who} lost ${e.value} charge to P${e.otherSlot + 1}`, tone: "loss" });
        break;
      case "OverlitCancelled":
        this.pushFeed({ key: e.key, tick: e.tick, text: `${who} Overlit ended`, tone: "info" });
        break;
      default:
        break; // RoundFinished and unknown kinds change no visual here; the final flag and score already tell the truth
    }
  }

  private pushFeed(line: FeedLine): void {
    this.feed = [line, ...this.feed].slice(0, FEED_LENGTH);
  }

  // ---- per displayed frame ---------------------------------------------------------------------------------------------------
  /** Smoothed draw position of every known slot at local time `nowMs`. Call once per rendered frame, before reading `hud()`. */
  render(nowMs: number, dtMs: number): ReadonlyMap<number, Vec> {
    const f = this.latest;
    if (f) {
      const lp = this.local.update(dtMs);
      if (lp) this.drawn.set(f.you.slot, lp);
      for (const [slot, track] of this.remotes) {
        const p = track.sample(nowMs);
        if (p) this.drawn.set(slot, p);
      }
      const pm = this.pendingMove;
      if (pm && pm.authDone && lp && Math.hypot(lp.x - pm.x, lp.y - pm.y) > 0.5) {
        this.metrics = { ...this.metrics, moveToDrawnMs: nowMs - pm.t0 };
        this.pendingMove = null;
      }
    }
    // Burst that the server never resolved is reported, not left "requested" forever.
    if (this.burst && (this.burst.stage === "requested" || this.burst.stage === "admitted") && nowMs - this.burst.atMs > BURST_CONFIRM_WINDOW_MS) {
      const me = f?.players.find((p) => p.slot === f.you.slot);
      this.burst = { ...this.burst, stage: "not_confirmed", detail: me && me.charge < 4 ? `No Burst: charge ${me.charge}/4` : "Burst not confirmed by the server", atMs: nowMs };
      this.dirty = true;
    }
    // React hears about the session at most 10 times a second (timer, scores, feed, connection); the world is drawn every frame by Pixi.
    if (nowMs - this.lastNotifyMs >= 100) {
      this.lastNotifyMs = nowMs;
      this.dirty = false;
      this.metrics = { ...this.metrics, visualLagUnits: this.local.lag };
      for (const l of this.listeners) l();
    }
    return this.drawn;
  }

  /** True when the transport claims to be live but no frame arrived for STALL_MS. */
  stalled(nowMs: number): boolean {
    return !!this.latest && !this.latest.final && this.connection.kind === "live" && nowMs - this.latest.receivedAtMs > STALL_MS;
  }

  hud(nowMs: number): PlayHud {
    const f = this.latest;
    const stalled = this.stalled(nowMs);
    // A stall is a connection fact, shown as such even when the transport itself has not reported a failure yet.
    const connection: ConnectionState = stalled && this.connection.kind === "live"
      ? { kind: "degraded", reason: `No frames for ${((nowMs - f!.receivedAtMs) / 1000).toFixed(1)} s`, sinceMs: f!.receivedAtMs }
      : this.connection;
    return {
      transportLabel: this.transport.label,
      production: this.transport.production,
      connection,
      stalled,
      identity: f?.identity ?? null,
      tick: f?.tick ?? 0,
      tickRate: f?.tickRate ?? 30,
      roundTicks: f?.roundTicks ?? 2700,
      secondsLeft: f ? Math.max(0, (f.roundTicks - f.tick) / f.tickRate) : null,
      final: f?.final ?? false,
      players: f?.players ?? [],
      you: f?.you ?? null,
      burst: this.burst,
      feed: this.feed,
      eventsComplete: f?.eventsComplete ?? true,
      metrics: this.metrics,
      preset: this.preset,
    };
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const off of this.off) off();
    this.listeners.clear();
    this.eventListeners.clear();
    this.transport.dispose();
  }
}
