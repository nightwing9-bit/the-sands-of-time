// Validates: Spec 13 task 13.7 (R13.20 to R13.23). The AudioDirector state machine against a FAKE ENGINE (the only honest seam for a unit test:
// no sound is produced, so nothing here says the mix is good; it checks routing, sequencing, de-duplication and cleanup).
import assert from "node:assert/strict";
import { existsSync, readFileSync, statSync } from "node:fs";
import { test } from "node:test";
import type { AudioBackend, CueLoadState, PlayHandle, PlayOptions } from "../src/audio/backend.ts";
import { AMBIENCE_SEQUENCE, CUES, CUE_IDS, type BusId, type CueId } from "../src/audio/cues.ts";
import { AudioDirector, SFX_VOICE_CAP, UI_CLICK_MIN_GAP_MS, type StorageLike } from "../src/audio/AudioDirector.ts";
import { STALE_EVENT_TICKS, attachPlayAudio, sfxKindFor } from "../src/audio/playAudio.ts";
import { PlaySession } from "../src/play/PlaySession.ts";
import type { ConnectionState, InputAnswerView, PlayEvent, PlayFrame, PlayTransport, PlayerFrame } from "../src/play/playTypes.ts";
import type { ControlIntent } from "../src/presentation/types.ts";
import { SOUND_FILES } from "../src/config/assetRegistry.ts";

class FakeHandle implements PlayHandle {
  live = true;
  stops: number[] = [];
  db: number;
  readonly cue: CueId;
  readonly options: PlayOptions;
  constructor(cue: CueId, options: PlayOptions) { this.cue = cue; this.options = options; this.db = options.gainDb; }
  stop(fadeOutMs: number) { if (!this.live) return; this.live = false; this.stops.push(fadeOutMs); }
  setGainDb(db: number) { this.db = db; }
  /** The source reaches its natural end (what the browser's `ended` event does). A stopped handle never reports completion. */
  finish() { if (!this.live) return; this.live = false; this.options.onEnded?.(); }
}

class FakeBackend implements AudioBackend {
  unlocked = false;
  handles: FakeHandle[] = [];
  gains: Record<BusId, number> = { music: 1, sfx: 1 };
  preloads: (readonly CueId[])[] = [];
  disposed = false;
  private listeners = new Set<() => void>();
  async unlock() { this.unlocked = true; return true; }
  setBusGain(bus: BusId, gain: number) { this.gains[bus] = gain; }
  preload(order: readonly CueId[]) { this.preloads.push(order); }
  cueState(): CueLoadState { return "ready"; }
  /** Like the real engine, starting a source makes it announce a load-state change BEFORE play() returns (this once caused unbounded recursion). */
  play(cue: CueId, options: PlayOptions): PlayHandle { const h = new FakeHandle(cue, options); this.handles.push(h); for (const l of this.listeners) l(); return h; }
  onChange(l: () => void) { this.listeners.add(l); return () => this.listeners.delete(l); }
  dispose() { this.disposed = true; for (const h of this.handles) h.stop(0); }
  live(bus?: BusId, loop?: boolean) { return this.handles.filter((h) => h.live && (bus === undefined || h.options.bus === bus) && (loop === undefined || h.options.loop === loop)); }
  liveCues(bus?: BusId, loop?: boolean): CueId[] { return this.live(bus, loop).map((h) => h.cue); }
  /** Every handle ever started for a cue. */
  starts(cue: CueId) { return this.handles.filter((h) => h.cue === cue).length; }
}

async function setup(opts: { unlock?: boolean; storage?: StorageLike; now?: () => number } = {}) {
  const backend = new FakeBackend();
  const director = new AudioDirector(backend, { ...(opts.storage ? { storage: opts.storage } : {}), ...(opts.now ? { now: opts.now } : {}) });
  if (opts.unlock !== false) await director.unlock();
  return { backend, director };
}

test("exactly the nine approved files are registered, and each is served from public/game-assets/sounds", () => {
  assert.deepEqual(Object.values(SOUND_FILES).map((u) => u.split("/").pop()).sort(), [
    "Ghost_knocking.wav", "Gushy_wind.mp3", "blast.wav", "candle_eat.wav", "gameLoading.wav", "ghost_main_theme.wav", "menu.wav", "multiplayer_theme.wav", "player_hit.wav",
  ]);
  assert.equal(CUE_IDS.length, 9);
  for (const id of CUE_IDS) {
    assert.ok(CUES[id].url.startsWith("/game-assets/sounds/"), "root-relative served path, never file:// or /Users");
    const served = new URL(`../public${CUES[id].url}`, import.meta.url);
    assert.ok(existsSync(served), `${CUES[id].url} is served`);
    const master = new URL(`../../../../design-inputs/game-assets/sounds/${CUES[id].url.split("/").pop()}`, import.meta.url);
    // The masters live outside the app; where this checkout has them, the served copy must be a byte-identical, unaltered copy.
    if (existsSync(master)) assert.deepEqual(readFileSync(served), readFileSync(master), `${id} is an unmodified copy of its master`);
    else assert.ok(statSync(served).size > 0);
  }
});

test("before the browser allows sound nothing starts and effects are dropped; unlock starts the menu loop once", async () => {
  const { backend, director } = await setup({ unlock: false });
  assert.equal(director.playEvent({ key: "k1", kind: "candle" }), false);
  assert.equal(backend.handles.length, 0);
  const claim = director.claim("day");
  assert.equal(backend.handles.length, 0, "a locked context never queues loops");
  await director.unlock();
  assert.deepEqual(backend.liveCues("music", true), ["multiplayerTheme"], "the mode asked for while locked starts at unlock");
  assert.equal(backend.preloads.length, 1);
  await director.unlock();
  assert.equal(backend.starts("multiplayerTheme"), 1, "a second unlock does not restart the music");
  claim.release();
});

test("music follows the claimed mode and there is never more than one live BGM", async () => {
  const { backend, director } = await setup();
  assert.deepEqual(backend.liveCues("music", true), ["gameLoading"]);
  const claim = director.claim("day");
  assert.deepEqual(backend.liveCues("music", true), ["multiplayerTheme"]);
  claim.set("transition");
  assert.deepEqual(backend.liveCues("music"), [], "transition is silent: no new theme, nothing invented");
  claim.set("night");
  assert.deepEqual(backend.liveCues("music", true), ["ghostMainTheme"]);
  claim.release();
  assert.deepEqual(backend.liveCues("music", true), ["gameLoading"], "leaving night returns to the menu loop");
  for (const h of backend.handles.filter((x) => x.options.loop)) assert.ok(h.stops.length === 0 || h.stops[0]! > 0, "loops leave with a fade, not a cut");
  assert.equal(backend.live("music").filter((h) => h.options.loop).length, 1);
});

test("night is accepted only straight after a transition; from menu or day it stays quiet and says why", async () => {
  const { backend, director } = await setup();
  const claim = director.claim("night");
  assert.equal(director.getStatus().mode, "transition");
  assert.equal(director.getStatus().nightBlocked, true);
  assert.deepEqual(backend.liveCues("music"), [], "no ghost theme and no ambience from a skipped or failed preparation");
  director.refresh();
  assert.equal(director.getStatus().mode, "transition", "re-evaluation does not let a refused night through");
  claim.set("day");
  claim.set("transition");
  claim.set("night");
  assert.equal(director.getStatus().mode, "night");
  assert.deepEqual(backend.liveCues("music", true), ["ghostMainTheme"]);
  assert.deepEqual(backend.liveCues("music", false), ["wind"]);
  claim.release();
});

test("night ambience is wind, wind, knock, repeat, each started only by the previous one's completion", async () => {
  const { backend, director } = await setup();
  const claim = director.claim("transition");
  claim.set("night");
  const order: CueId[] = [];
  for (let i = 0; i < 7; i += 1) {
    const live = backend.live("music", false);
    assert.equal(live.length, 1, "exactly one ambience source at any moment, never simultaneous wind and knock");
    order.push(live[0]!.cue);
    assert.equal(director.getStatus().ambienceStep, i % 3);
    live[0]!.finish();
  }
  assert.deepEqual(order, ["wind", "wind", "knocking", "wind", "wind", "knocking", "wind"]);
  assert.equal(director.getStatus().ambienceCompleted, 7);
  assert.deepEqual([...AMBIENCE_SEQUENCE], ["wind", "wind", "knocking"]);
  assert.equal(backend.liveCues("music", true).length, 1, "the ghost theme keeps looping underneath, once");
  claim.release();
});

test("leaving night stops the theme and ambience; a late completion cannot restart anything; re-entering starts one fresh sequence", async () => {
  const { backend, director } = await setup();
  const claim = director.claim("transition");
  claim.set("night");
  const staleWind = backend.live("music", false)[0]!;
  claim.set("day");
  assert.equal(backend.live("music", false).length, 0, "ambience is gone in the day");
  assert.equal(staleWind.live, false);
  staleWind.options.onEnded?.(); // the browser delivers a late `ended` after the sequence was cancelled
  assert.equal(backend.live("music", false).length, 0, "a stale callback starts nothing");
  assert.equal(director.getStatus().ambienceStep, null);
  claim.set("transition");
  claim.set("night");
  assert.equal(backend.live("music", false).length, 1);
  assert.equal(director.getStatus().ambienceStep, 0, "a new night begins at the first wind");
  staleWind.options.onEnded?.();
  assert.equal(backend.live("music", false).length, 1, "the old night's callback cannot advance or double the new sequence");
  assert.equal(director.getStatus().ambienceStep, 0);
  claim.release();
  assert.deepEqual(backend.liveCues("music", false), [], "nothing night-related survives a return to the menu");
  assert.deepEqual(backend.liveCues("music", true), ["gameLoading"]);
});

test("claims: the latest wins, release hands back to the one below, and repeated identical claims never restart music", async () => {
  const { backend, director } = await setup();
  const a = director.claim("day");
  const before = backend.starts("multiplayerTheme");
  a.set("day");
  director.refresh();
  assert.equal(backend.starts("multiplayerTheme"), before, "re-renders do not restart the theme");
  const b = director.claim("menu");
  assert.deepEqual(backend.liveCues("music", true), ["gameLoading"]);
  b.release();
  assert.deepEqual(backend.liveCues("music", true), ["multiplayerTheme"]);
  a.release();
  a.release(); // idempotent
  assert.deepEqual(backend.liveCues("music", true), ["gameLoading"]);
});

test("a candle, hit or Burst sounds once per stable key; duplicates, resyncs and ghost cues are silent", async () => {
  const { backend, director } = await setup();
  assert.equal(director.playEvent({ key: "m1|7:0", kind: "candle" }), true);
  assert.equal(director.playEvent({ key: "m1|7:0", kind: "candle" }), false, "same event again (resync/re-render)");
  assert.equal(director.playEvent({ key: "m1|7:1", kind: "candle" }), true, "a different event sounds");
  assert.equal(director.playEvent({ key: "m1|9:0", kind: "hit" }), true);
  assert.equal(director.playEvent({ key: "m1|9:0", kind: "burst" }), true, "identity is kind + key");
  assert.equal(director.playEvent({ key: "m1|12:0", kind: "candle", origin: "ghost" }), false, "ghost origin never sounds");
  assert.equal(backend.starts("candleEat"), 2);
  assert.equal(backend.starts("playerHit"), 1);
  assert.equal(backend.starts("blast"), 1);
  director.resetEvents();
  assert.equal(director.playEvent({ key: "m1|7:0", kind: "candle" }), true, "a new match identity forgets the old keys");
});

test("effects are dropped when muted or silent, bounded in polyphony, and the UI click is throttled", async () => {
  let now = 1000;
  const { backend, director } = await setup({ now: () => now });
  director.setSfxMuted(true);
  assert.equal(director.playEvent({ key: "a", kind: "candle" }), false);
  director.setSfxMuted(false);
  director.setSfxVolume(0);
  assert.equal(director.playEvent({ key: "b", kind: "candle" }), false);
  director.setSfxVolume(0.5);
  assert.equal(director.playEvent({ key: "a", kind: "candle" }), false, "a key seen while muted is not replayed later");
  for (let i = 0; i < SFX_VOICE_CAP + 4; i += 1) director.playEvent({ key: `burst-${i}`, kind: "burst" });
  assert.equal(backend.live("sfx").length, SFX_VOICE_CAP, "overlapping Bursts cannot pile up without bound");
  assert.equal(director.playUiClick(), false, "voice cap reached");
  for (const h of backend.live("sfx")) h.finish();
  assert.equal(director.playUiClick(), true);
  assert.equal(director.playUiClick(), false, "a second activation inside the gap is one click");
  now += UI_CLICK_MIN_GAP_MS;
  assert.equal(director.playUiClick(), true);
});

test("volume and mute reach the buses (music mute silences ambience too), persist, and survive bad stored data", async () => {
  const store = new Map<string, string>();
  const storage: StorageLike = { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => { store.set(k, v); } };
  const { backend, director } = await setup({ storage });
  const claim = director.claim("transition");
  claim.set("night");
  assert.ok(backend.gains.music > 0);
  director.setMusicMuted(true);
  assert.equal(backend.gains.music, 0, "wind, knock and theme all ride the music bus, so one mute silences all");
  assert.equal(backend.live("music", false).length, 1, "the sequence keeps its place while muted (no timers to cancel)");
  assert.ok(backend.gains.sfx > 0, "SFX are independent of the music mute");
  director.setMusicMuted(false);
  director.setMusicVolume(2);
  assert.equal(director.getStatus().settings.musicVolume, 1, "volume is clamped");
  claim.release();
  const again = new AudioDirector(new FakeBackend(), { storage });
  assert.equal(again.getStatus().settings.musicVolume, 1);
  store.set("ghost-lap.audio.v1", "{not json");
  assert.equal(new AudioDirector(new FakeBackend(), { storage }).getStatus().settings.musicVolume, 0.85, "corrupt storage falls back to defaults");
});

test("dispose stops every voice, invalidates ambience callbacks and ignores later calls", async () => {
  const { backend, director } = await setup();
  const claim = director.claim("transition");
  claim.set("night");
  director.playEvent({ key: "x", kind: "burst" });
  const wind = backend.live("music", false)[0]!;
  director.dispose();
  assert.equal(backend.live().length, 0);
  assert.equal(backend.disposed, true);
  wind.options.onEnded?.();
  assert.equal(backend.live().length, 0, "no segment starts after dispose");
  assert.equal(director.playEvent({ key: "y", kind: "burst" }), false);
  claim.set("day");
  assert.equal(backend.live().length, 0);
});

// ---- the bridge from real play events -----------------------------------------------------------------------------------------------------
const player = (slot: 0 | 1 | 2 | 3): PlayerFrame => ({ slot, x: 100, y: 100, charge: 0, overlit: false, overlitDeadlineTick: null, totalScore: 0, movementPoints: 0, pickupPoints: 0, penaltyPoints: 0, pickups: 0, bursts: 0, appliedSeq: -1 });
const ev = (key: string, kind: PlayEvent["kind"], slot: number, tick = 10): PlayEvent => ({ key, tick, kind, raw: kind, slot, otherSlot: 1, value: 1, candleId: "c1" });
const frame = (tick: number, events: PlayEvent[], over: Partial<PlayFrame> = {}): PlayFrame => ({
  identity: "match-a", tick, tickRate: 30, roundTicks: 2700, final: false, receivedAtMs: tick * 33, players: [player(0), player(1)], candles: [], events,
  you: { slot: 0, canControl: true }, eventsComplete: true, ...over,
});

class FakeTransport implements PlayTransport {
  readonly label = "fake";
  readonly production = false;
  private f?: (x: PlayFrame) => void;
  onFrame(l: (x: PlayFrame) => void) { this.f = l; return () => undefined; }
  onConnection(_l: (x: ConnectionState) => void) { return () => undefined; }
  onInputAnswer(_l: (x: InputAnswerView) => void) { return () => undefined; }
  setIntent(_i: ControlIntent) {}
  async start() {}
  dispose() {}
  push(x: PlayFrame) { this.f?.(x); }
}

test("event to sound mapping: local pickup, local weaker victim, any confirmed Burst; nothing else", () => {
  const f = frame(10, []);
  assert.equal(sfxKindFor(ev("a", "CandleCollected", 0), f), "candle");
  assert.equal(sfxKindFor(ev("a", "CandleCollected", 1), f), null, "another player's pickup does not sound");
  assert.equal(sfxKindFor(ev("b", "ContactDrain", 0), f), "hit");
  assert.equal(sfxKindFor(ev("b", "ContactDrain", 1), f), null, "only the weaker victim, and only the local one");
  assert.equal(sfxKindFor(ev("c", "BurstManual", 1), f), "burst");
  assert.equal(sfxKindFor(ev("d", "BurstAuto", 0), f), "burst");
  assert.equal(sfxKindFor(ev("e", "BurstHit", 0), f), null, "a Burst hit is not a weaker-contact hit");
  assert.equal(sfxKindFor(ev("f", "OverlitStarted", 0), f), null);
  assert.equal(sfxKindFor(ev("g", "CandleCollected", 0), frame(10, [], { you: { slot: -1, canControl: false } })), null, "a spectator has no pickups");
});

test("through the real PlaySession: each confirmed event sounds once, a repeated or resynced frame does not, and a new identity resets", async () => {
  const { backend, director } = await setup();
  const transport = new FakeTransport();
  const session = new PlaySession(transport);
  const off = attachPlayAudio(session, director);
  transport.push(frame(10, [ev("k1", "CandleCollected", 0), ev("k2", "BurstManual", 1), ev("k3", "ContactDrain", 0), ev("k4", "BurstHit", 0)]));
  assert.equal(backend.starts("candleEat"), 1);
  assert.equal(backend.starts("blast"), 1);
  assert.equal(backend.starts("playerHit"), 1);
  transport.push(frame(11, [ev("k1", "CandleCollected", 0), ev("k2", "BurstManual", 1)]));
  transport.push(frame(12, [ev("k1", "CandleCollected", 0)], { snapshotSequence: 3 }));
  assert.equal(backend.starts("candleEat"), 1, "the same server event key is never sounded twice");
  assert.equal(backend.starts("blast"), 1);
  transport.push(frame(13, [ev("old", "CandleCollected", 0, 13 - STALE_EVENT_TICKS - 1)]));
  assert.equal(backend.starts("candleEat"), 1, "a backlog event delivered long after it happened is history, not a sound");
  transport.push(frame(1, [ev("k1", "CandleCollected", 0, 1)], { identity: "match-b" }));
  assert.equal(backend.starts("candleEat"), 2, "a rematch (new identity) may reuse a key");
  off();
  transport.push(frame(2, [ev("z", "BurstManual", 0, 2)], { identity: "match-b" }));
  assert.equal(backend.starts("blast"), 1, "detached bridge makes no sound");
});
