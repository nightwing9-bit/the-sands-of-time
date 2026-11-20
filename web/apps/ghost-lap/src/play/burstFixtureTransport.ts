// PREVIEW FIXTURE TRANSPORT (development only, 13.6). A scripted source of frames in the exact PlayFrame shape a real transport produces, so the
// Burst study drives the REAL path: PlaySession -> cues -> PlayScene (faces, halo pulse, shockwave). It is NOT a server, NOT a saved history and
// it can never certify live play: the label says so and `production` is false, which the HUD shows.
//
// What it imitates, with the server rule it follows (src/GhostLap/Rules/GameRulesV1.cs, Simulation/GhostLapTransition.cs Fire):
//   * a Burst needs the owner at charge 4; it resets the owner to 0; every other player whose centre is within 160 units (inclusive) gets a
//     BurstHit (score penalty 90 clamped at their total) and loses 2 charge; the event value of the Burst is the number of hits;
//   * a manual press is "admitted" first and only the BurstManual event confirms it; an Overlit auto-Burst is a BurstAuto with no press.
// Candles are fixture candles at real ArenaV1 sites with fixture ids; positions are static lanes between the nine walls.
import type { ActorSlot, Charge, ControlIntent } from "../presentation/types.ts";
import type { ConnectionState, InputAnswerView, PlayEvent, PlayEventKind, PlayFrame, PlayTransport, PlayerFrame } from "./playTypes.ts";
import { ARENA_V1_CANDLE_SITES } from "../arena/candleSites.ts";
import { BURST_REACH_UNITS } from "../fx/effectPresets.ts";

const TICK_MS = 1000 / 30;
const START: readonly { slot: ActorSlot; x: number; y: number; charge: Charge; score: number }[] = [
  { slot: 0, x: 620, y: 450, charge: 4, score: 420 },
  { slot: 1, x: 700, y: 380, charge: 3, score: 300 },
  { slot: 2, x: 560, y: 560, charge: 2, score: 260 },
  { slot: 3, x: 1100, y: 450, charge: 3, score: 340 },
];
const FIXTURE_CANDLES = ARENA_V1_CANDLE_SITES
  .map((s, i) => ({ s, i, d: Math.hypot(s.x - 620, s.y - 450) }))
  .sort((a, b) => a.d - b.d)
  .slice(0, 7)
  .map(({ s, i }) => ({ id: `fixture-candle-${i}`, x: s.x, y: s.y, phase: "active" as const }));

export class BurstFixtureTransport implements PlayTransport {
  readonly label = "PREVIEW FIXTURE · scripted frames in the real PlayFrame shape · not live play";
  readonly production = false;
  private frameListeners = new Set<(f: PlayFrame) => void>();
  private connListeners = new Set<(c: ConnectionState) => void>();
  private answerListeners = new Set<(a: InputAnswerView) => void>();
  private timer: ReturnType<typeof setInterval> | null = null;
  private timeouts = new Set<ReturnType<typeof setTimeout>>();
  private match = 0;
  private tick = 0;
  private seq = 0;
  private players: { slot: ActorSlot; x: number; y: number; charge: Charge; score: number; bursts: number; overlit: boolean }[] = [];
  private pending: { kind: PlayEventKind; slot: number; otherSlot: number; value: number }[] = [];
  private lastBurst = false;
  private disposed = false;
  /** Bursts the fixture has resolved (the preview reads it; it is not game state). */
  burstsFired = 0;

  constructor() { this.resetPlayers(); }

  private resetPlayers(): void {
    this.players = START.map((p) => ({ ...p, bursts: 0, overlit: p.charge === 4 }));
  }
  get identity(): string { return `preview-fixture-${this.match}`; }

  onFrame(l: (f: PlayFrame) => void) { this.frameListeners.add(l); return () => this.frameListeners.delete(l); }
  onConnection(l: (c: ConnectionState) => void) { this.connListeners.add(l); return () => this.connListeners.delete(l); }
  onInputAnswer(l: (a: InputAnswerView) => void) { this.answerListeners.add(l); return () => this.answerListeners.delete(l); }

  async start(): Promise<void> {
    if (this.disposed || this.timer) return;
    for (const l of this.connListeners) l({ kind: "live" });
    this.emit();
    this.timer = setInterval(() => this.emit(), TICK_MS);
  }

  setIntent(intent: ControlIntent): void {
    const rising = intent.requestBurst && !this.lastBurst;
    this.lastBurst = intent.requestBurst;
    if (!rising || this.disposed) return;
    const sentAtMs = performance.now();
    const seq = ++this.seq;
    this.later(90, () => {
      for (const l of this.answerListeners) l({ seq, accepted: true, rejection: null, burstRequested: true, sentAtMs, answeredAtMs: performance.now() });
      // Admission is not proof of a Burst: the server resolves it on the next tick, and only with full charge.
      this.later(TICK_MS, () => { if (this.players[0]!.charge === 4) this.fireBurst(0, "BurstManual"); });
    });
  }

  /** An Overlit auto-Burst by `slot`: no press, no admission. The fixture arms the slot first so the scene is always demonstrable. */
  autoBurst(slot: ActorSlot = 0): void {
    const p = this.players[slot]!;
    p.charge = 4;
    p.overlit = true;
    this.fireBurst(slot, "BurstAuto");
  }
  /** Restores every player to the starting charge and score (a stand-in for "collect candles again"). */
  rearm(): void {
    this.resetPlayers();
    this.emit();
  }
  /** One weaker-contact event: slot 2 (charge 2) loses 1 charge to slot 1 (stronger). The only face it changes is the victim's. */
  contact(): void {
    const v = this.players[2]!;
    v.charge = Math.max(0, v.charge - 1) as Charge;
    this.pending.push({ kind: "ContactDrain", slot: 2, otherSlot: 1, value: 1 });
  }
  /** A new match: new identity, everything of the old one must disappear (heads, filters, remembered keys). */
  newMatch(): void {
    this.match += 1;
    this.tick = 0;
    this.pending = [];
    this.resetPlayers();
    this.emit();
  }

  private fireBurst(slot: ActorSlot, kind: "BurstManual" | "BurstAuto"): void {
    const owner = this.players[slot]!;
    if (owner.charge !== 4) return;
    const hits: { victim: number; penalty: number }[] = [];
    for (const t of this.players) {
      if (t.slot === slot) continue;
      if (Math.hypot(t.x - owner.x, t.y - owner.y) <= BURST_REACH_UNITS) hits.push({ victim: t.slot, penalty: Math.min(90, t.score) });
    }
    this.pending.push({ kind, slot, otherSlot: 0, value: hits.length });
    for (const h of hits) {
      const t = this.players[h.victim]!;
      t.score -= h.penalty;
      t.charge = Math.max(0, t.charge - 2) as Charge;
      this.pending.push({ kind: "BurstHit", slot: h.victim, otherSlot: slot, value: h.penalty });
    }
    owner.charge = 0;
    owner.overlit = false;
    owner.bursts += 1;
    this.burstsFired += 1;
  }

  private later(ms: number, fn: () => void): void {
    const id = setTimeout(() => { this.timeouts.delete(id); if (!this.disposed) fn(); }, ms);
    this.timeouts.add(id);
  }

  private emit(): void {
    if (this.disposed) return;
    this.tick += 1;
    const tick = this.tick;
    const events: PlayEvent[] = this.pending.map((e, i) => ({ key: `${this.identity}:${tick}:${i}`, tick, kind: e.kind, raw: e.kind, slot: e.slot, otherSlot: e.otherSlot, value: e.value, candleId: "" }));
    this.pending = [];
    const players: PlayerFrame[] = this.players.map((p) => ({
      slot: p.slot, x: p.x, y: p.y, charge: p.charge, overlit: p.overlit, overlitDeadlineTick: p.overlit ? tick + 240 : null, totalScore: p.score,
      movementPoints: 0, pickupPoints: p.score, penaltyPoints: 0, pickups: 0, bursts: p.bursts, appliedSeq: this.seq,
    }));
    const frame: PlayFrame = {
      identity: this.identity, tick, tickRate: 30, roundTicks: 2700, final: false, receivedAtMs: performance.now(), players,
      candles: FIXTURE_CANDLES, events, you: { slot: 0, canControl: true }, eventsComplete: true,
    };
    for (const l of this.frameListeners) l(frame);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    for (const id of this.timeouts) clearTimeout(id);
    this.timeouts.clear();
    this.frameListeners.clear();
    this.connListeners.clear();
    this.answerListeners.clear();
  }
}
