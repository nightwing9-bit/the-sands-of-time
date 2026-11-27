// The ONE app-lifetime audio owner (Spec 13, task 13.7; design section 9; skill ref 07). Rules only: it knows modes, cues and events and talks to a
// sound engine through AudioBackend, so the whole state machine is unit-tested with a fake engine. No React, no Pixi, no DOM.
//
//   claims (screens)  ->  effective mode  ->  music loop (+ night ambience)         one music handle at a time (a 400 ms fade overlaps two)
//   real game events  ->  playEvent(key)  ->  one SFX per stable key               ghost/historical origin never sounds
//   UI activation     ->  playUiClick()   ->  menu.wav, throttled
//
// Modes: menu = gameLoading.wav; day = multiplayer_theme.wav; transition = silent music (no new theme, no invented sound); night = ghost_main_theme.wav
// PLUS the ambience sequence wind, wind, knock. Night is accepted ONLY directly after `transition` (the prepared/ready gate): a claim for night from
// menu or day stays in `transition` (quiet) and reports `nightBlocked`, so a failed or skipped preparation can never start the ghost theme.
//
// The ambience is sequenced by the engine's natural-end callback (PlayOptions.onEnded). A generation number invalidates every pending callback the
// moment night ends, so a late `ended` can neither start a segment in the menu nor start a second sequence.
// A hidden tab is never a pause here. Mute is a gain of zero: the sequence keeps its place (no timers exist to cancel) and nothing is audible.
// Autoplay restriction: before unlock nothing is started and SFX are dropped; gameplay never waits for sound.
import type { AudioBackend, CueLoadState, PlayHandle } from "./backend.ts";
import { AMBIENCE_SEQUENCE, CUES, CUE_IDS, MUSIC_FOR_MODE, PRELOAD_ORDER, SFX_CUE, type AudioMode, type CueId, type SfxKind } from "./cues.ts";

export type { AudioMode, SfxKind } from "./cues.ts";

/** Fades are DIRECTOR BASELINES (skill ref 07: 250 to 500 ms), not listened values. */
export const MUSIC_FADE_IN_MS = 400;
export const MUSIC_FADE_OUT_MS = 400;
export const AMBIENCE_FADE_OUT_MS = 300;
/** A few ms so a segment never starts with a click. */
export const AMBIENCE_DECLICK_MS = 12;
export const SFX_VOICE_CAP = 8;
export const UI_CLICK_MIN_GAP_MS = 120;
const SEEN_LIMIT = 512;
/** Headroom so music + ambience + a Burst cannot sum past full scale in the worst case. Provisional, with the trims in cues.ts. */
export const MUSIC_BUS_DB = -4;
export const SFX_BUS_DB = -3;
const SETTINGS_KEY = "ghost-lap.audio.v1";

export interface AudioSettings { readonly musicVolume: number; readonly sfxVolume: number; readonly musicMuted: boolean; readonly sfxMuted: boolean }
export const DEFAULT_SETTINGS: AudioSettings = { musicVolume: 0.85, sfxVolume: 0.9, musicMuted: false, sfxMuted: false };

export interface StorageLike { getItem(key: string): string | null; setItem(key: string, value: string): void }

export interface SfxEvent {
  /** Stable identity of the real event (the server's key, already scoped by match identity). Never invented from scores. */
  readonly key: string;
  readonly kind: SfxKind;
  /** Historical/ghost cues are never living sounds. Anything but "ghost" is treated as living. */
  readonly origin?: "living" | "ghost";
}

export interface ModeClaim {
  set(mode: AudioMode): void;
  release(): void;
}

export interface AudioStatus {
  readonly unlocked: boolean;
  readonly mode: AudioMode;
  /** What the top claim asked for (differs from `mode` when night was refused). */
  readonly requestedMode: AudioMode;
  readonly nightBlocked: boolean;
  readonly musicCue: CueId | null;
  /** 0-based index into wind, wind, knock of the running ambience segment; null when there is no ambience. */
  readonly ambienceStep: number | null;
  readonly ambienceCue: CueId | null;
  /** Ambience segments that played to their natural end since night began. */
  readonly ambienceCompleted: number;
  readonly liveSfx: number;
  /** How many times each cue was started (SFX events, ambience segments and music starts). */
  readonly started: Readonly<Record<CueId, number>>;
  readonly cueState: Readonly<Record<CueId, CueLoadState>>;
  readonly settings: AudioSettings;
  readonly trimDb: Readonly<Record<CueId, number>>;
}

const clamp01 = (v: number): number => (Number.isFinite(v) ? Math.min(1, Math.max(0, v)) : 0);
const dbToGain = (db: number): number => 10 ** (db / 20);
/** Slider position to gain: a squared taper, so the lower half of the slider is usable. */
export const sliderToGain = (v: number): number => clamp01(v) ** 2;

function emptyCounts(): Record<CueId, number> {
  return Object.fromEntries(CUE_IDS.map((id) => [id, 0])) as Record<CueId, number>;
}

export class AudioDirector {
  private readonly backend: AudioBackend;
  private readonly storage: StorageLike | null;
  private readonly clock: () => number;
  private settings: AudioSettings;
  private claims: { mode: AudioMode }[] = [];
  private effective: AudioMode = "menu";
  private requested: AudioMode = "menu";
  private nightBlocked = false;
  private music: { cue: CueId; handle: PlayHandle } | null = null;
  private ambience: { step: number; handle: PlayHandle | null } | null = null;
  private ambienceGen = 0;
  private ambienceCompleted = 0;
  private sfx: PlayHandle[] = [];
  private readonly seen = new Set<string>();
  private readonly seenOrder: string[] = [];
  private readonly started = emptyCounts();
  private trimOverrides: Partial<Record<CueId, number>> = {};
  private lastClickAt = Number.NEGATIVE_INFINITY;
  private readonly listeners = new Set<() => void>();
  private snapshot: AudioStatus | null = null;
  private disposed = false;
  private reconciling = false;
  private readonly offBackend: () => void;

  constructor(backend: AudioBackend, options: { storage?: StorageLike | null; now?: () => number } = {}) {
    this.backend = backend;
    this.storage = options.storage ?? null;
    this.clock = options.now ?? (() => performance.now());
    this.settings = this.loadSettings();
    this.pushBusGains(0);
    this.offBackend = backend.onChange(() => { this.pushBusGains(0); this.reconcile(); this.touch(); });
  }

  // ---- React store ---------------------------------------------------------------------------------------------------------------
  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  /** Stable between changes (useSyncExternalStore requires it). */
  readonly getStatus = (): AudioStatus => {
    if (this.snapshot) return this.snapshot;
    const cueState = {} as Record<CueId, CueLoadState>;
    const trimDb = {} as Record<CueId, number>;
    for (const id of CUE_IDS) { cueState[id] = this.backend.cueState(id); trimDb[id] = this.trim(id); }
    this.snapshot = {
      unlocked: this.backend.unlocked,
      mode: this.effective,
      requestedMode: this.requested,
      nightBlocked: this.nightBlocked,
      musicCue: this.music?.handle.live ? this.music.cue : null,
      ambienceStep: this.ambience ? this.ambience.step : null,
      ambienceCue: this.ambience ? AMBIENCE_SEQUENCE[this.ambience.step] ?? null : null,
      ambienceCompleted: this.ambienceCompleted,
      liveSfx: this.sfx.filter((h) => h.live).length,
      started: { ...this.started },
      cueState,
      settings: this.settings,
      trimDb,
    };
    return this.snapshot;
  };

  /** Re-publish the status (live voice counts change without an event of ours). Used by the dev study's readout timer. */
  refresh(): void { this.touch(); }

  private touch(): void {
    this.snapshot = null;
    for (const l of this.listeners) l();
  }

  // ---- unlock --------------------------------------------------------------------------------------------------------------------
  /** Call from a real user gesture. Safe to call again: it only resumes, then starts whatever the current mode wants. */
  async unlock(): Promise<boolean> {
    if (this.disposed) return false;
    const ok = await this.backend.unlock();
    if (this.disposed) return false;
    if (ok) {
      this.pushBusGains(0);
      this.backend.preload(PRELOAD_ORDER);
    }
    this.reconcile();
    this.touch();
    return ok;
  }

  // ---- modes ---------------------------------------------------------------------------------------------------------------------
  /**
   * A screen/scene declares what it needs; the latest claim wins and releasing it hands the sound back to the one below (menu when none).
   * Claims instead of one global setter so a screen that unmounts can never be overridden by, or override, the screen that replaced it.
   */
  claim(mode: AudioMode): ModeClaim {
    const entry = { mode };
    this.claims.push(entry);
    this.recompute();
    return {
      set: (next) => { if (this.claims.includes(entry) && entry.mode !== next) { entry.mode = next; this.recompute(); } },
      release: () => {
        const at = this.claims.indexOf(entry);
        if (at >= 0) { this.claims.splice(at, 1); this.recompute(); }
      },
    };
  }

  private recompute(): void {
    if (this.disposed) return;
    const requested = this.claims.length > 0 ? this.claims[this.claims.length - 1]!.mode : "menu";
    this.requested = requested;
    let next: AudioMode;
    if (requested !== "night") {
      this.nightBlocked = false;
      next = requested;
    } else if (this.nightBlocked) {
      next = "transition";
    } else if (this.effective === "transition" || this.effective === "night") {
      next = "night";
    } else {
      this.nightBlocked = true; // night straight from menu/day: preparation was skipped or failed, so stay quiet until the claim leaves night
      next = "transition";
    }
    this.effective = next;
    this.reconcile();
    this.touch();
  }

  /** Bring the engine in line with the effective mode. Idempotent: re-renders and repeated calls change nothing once in line. */
  private reconcile(): void {
    if (this.disposed || this.reconciling || !this.backend.unlocked) return;
    // play() makes the engine start a load and announce it (onChange) before play() has returned; without this guard that announcement would
    // re-enter here while `this.music` is still unset and start the same loop again, without end.
    this.reconciling = true;
    try { this.reconcileNow(); } finally { this.reconciling = false; }
  }

  private reconcileNow(): void {
    const want = MUSIC_FOR_MODE[this.effective];
    // A loop never ends by itself; a handle that is no longer live failed to load. It is NOT retried on every change (that would be a retry storm).
    if (this.music && this.music.cue !== want) {
      this.music.handle.stop(MUSIC_FADE_OUT_MS);
      this.music = null;
    }
    if (want && !this.music) {
      const handle = this.backend.play(want, { bus: "music", loop: true, gainDb: this.trim(want), fadeInMs: MUSIC_FADE_IN_MS });
      this.music = { cue: want, handle };
      this.started[want] += 1;
    }
    const wantAmbience = this.effective === "night";
    if (wantAmbience && !this.ambience) this.startAmbience();
    else if (!wantAmbience && this.ambience) this.stopAmbience();
  }

  // ---- night ambience ------------------------------------------------------------------------------------------------------------
  private startAmbience(): void {
    const gen = ++this.ambienceGen;
    this.ambienceCompleted = 0;
    this.ambience = { step: 0, handle: null };
    this.playAmbienceStep(gen, 0);
  }

  private playAmbienceStep(gen: number, step: number): void {
    if (this.disposed || gen !== this.ambienceGen || !this.ambience) return;
    const cue = AMBIENCE_SEQUENCE[step]!;
    // Each segment starts only from the previous one's natural end (or the start of night); there is never more than one ambience source.
    this.ambience.step = step;
    this.ambience.handle = this.backend.play(cue, {
      bus: "music", loop: false, gainDb: this.trim(cue), fadeInMs: AMBIENCE_DECLICK_MS,
      onEnded: () => this.onAmbienceEnded(gen, step),
    });
    this.started[cue] += 1;
    this.touch();
  }

  private onAmbienceEnded(gen: number, step: number): void {
    if (this.disposed || gen !== this.ambienceGen || !this.ambience || this.ambience.step !== step) return; // stale callback of an earlier night
    this.ambienceCompleted += 1;
    this.playAmbienceStep(gen, (step + 1) % AMBIENCE_SEQUENCE.length);
  }

  private stopAmbience(): void {
    this.ambienceGen += 1; // every pending callback of the old sequence is now dead
    this.ambience?.handle?.stop(AMBIENCE_FADE_OUT_MS);
    this.ambience = null;
  }

  /** DEV listening aid: cut the running ambience segment short and go to the next step of the same sequence. */
  skipAmbienceStep(): boolean {
    if (!this.ambience) return false;
    const gen = this.ambienceGen;
    const next = (this.ambience.step + 1) % AMBIENCE_SEQUENCE.length;
    this.ambience.handle?.stop(60);
    this.playAmbienceStep(gen, next);
    return true;
  }

  // ---- one-shot sounds -----------------------------------------------------------------------------------------------------------
  /**
   * A real, already-confirmed game event. Plays at most once per `kind:key`; ghost-origin events never play. Returns whether a sound started.
   * Dropped silently when sound is locked or muted: gameplay never depends on it.
   */
  playEvent(event: SfxEvent): boolean {
    if (this.disposed || event.origin === "ghost") return false;
    const id = `${event.kind}:${event.key}`;
    if (this.seen.has(id)) return false;
    this.seen.add(id);
    this.seenOrder.push(id);
    if (this.seenOrder.length > SEEN_LIMIT) this.seen.delete(this.seenOrder.shift()!);
    return this.fire(SFX_CUE[event.kind]);
  }

  /** A new match/run identity: remembered keys of the old one must not suppress (or replay) anything. */
  resetEvents(): void {
    this.seen.clear();
    this.seenOrder.length = 0;
  }

  /** menu.wav for a real UI activation. Throttled so a held key or a double activation is one click. */
  playUiClick(): boolean {
    if (this.disposed) return false;
    const now = this.clock();
    if (now - this.lastClickAt < UI_CLICK_MIN_GAP_MS) return false;
    const sounded = this.fire("menu");
    if (sounded) this.lastClickAt = now; // only a click that actually sounded starts the gap
    return sounded;
  }

  private fire(cue: CueId): boolean {
    if (!this.backend.unlocked || this.settings.sfxMuted || this.settings.sfxVolume <= 0) return false;
    this.sfx = this.sfx.filter((h) => h.live);
    if (this.sfx.length >= SFX_VOICE_CAP) return false;
    this.sfx.push(this.backend.play(cue, { bus: "sfx", loop: false, gainDb: this.trim(cue), fadeInMs: 0 }));
    this.started[cue] += 1;
    this.touch();
    return true;
  }

  // ---- settings ------------------------------------------------------------------------------------------------------------------
  setMusicVolume(v: number): void { this.update({ musicVolume: clamp01(v) }); }
  setSfxVolume(v: number): void { this.update({ sfxVolume: clamp01(v) }); }
  setMusicMuted(muted: boolean): void { this.update({ musicMuted: muted }); }
  setSfxMuted(muted: boolean): void { this.update({ sfxMuted: muted }); }

  private update(patch: Partial<AudioSettings>): void {
    this.settings = { ...this.settings, ...patch };
    this.pushBusGains(60);
    try { this.storage?.setItem(SETTINGS_KEY, JSON.stringify(this.settings)); } catch { /* storage may be blocked; the setting still applies for this session */ }
    this.touch();
  }

  private pushBusGains(rampMs: number): void {
    const s = this.settings;
    this.backend.setBusGain("music", s.musicMuted ? 0 : sliderToGain(s.musicVolume) * dbToGain(MUSIC_BUS_DB), rampMs);
    this.backend.setBusGain("sfx", s.sfxMuted ? 0 : sliderToGain(s.sfxVolume) * dbToGain(SFX_BUS_DB), rampMs);
  }

  private loadSettings(): AudioSettings {
    try {
      const raw = this.storage?.getItem(SETTINGS_KEY);
      if (!raw) return DEFAULT_SETTINGS;
      const p = JSON.parse(raw) as Partial<Record<keyof AudioSettings, unknown>>;
      return {
        musicVolume: typeof p.musicVolume === "number" ? clamp01(p.musicVolume) : DEFAULT_SETTINGS.musicVolume,
        sfxVolume: typeof p.sfxVolume === "number" ? clamp01(p.sfxVolume) : DEFAULT_SETTINGS.sfxVolume,
        musicMuted: p.musicMuted === true,
        sfxMuted: p.sfxMuted === true,
      };
    } catch {
      return DEFAULT_SETTINGS;
    }
  }

  // ---- dev listening aid: per-cue trim ---------------------------------------------------------------------------------------------
  private trim(cue: CueId): number { return this.trimOverrides[cue] ?? CUES[cue].trimDb; }

  /** DEV: change one cue's trim live (session only, never saved). The listener reports the number; cues.ts is then edited. */
  setCueTrim(cue: CueId, db: number): void {
    if (!Number.isFinite(db)) return;
    this.trimOverrides = { ...this.trimOverrides, [cue]: Math.min(12, Math.max(-24, db)) };
    if (this.music?.cue === cue) this.music.handle.setGainDb(this.trim(cue));
    if (this.ambience?.handle && AMBIENCE_SEQUENCE[this.ambience.step] === cue) this.ambience.handle.setGainDb(this.trim(cue));
    this.touch();
  }
  resetCueTrims(): void {
    this.trimOverrides = {};
    if (this.music) this.music.handle.setGainDb(this.trim(this.music.cue));
    if (this.ambience?.handle) this.ambience.handle.setGainDb(this.trim(AMBIENCE_SEQUENCE[this.ambience.step]!));
    this.touch();
  }

  // ---- end of life ---------------------------------------------------------------------------------------------------------------
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.ambienceGen += 1;
    this.music?.handle.stop(0);
    this.ambience?.handle?.stop(0);
    for (const h of this.sfx) h.stop(0);
    this.music = null;
    this.ambience = null;
    this.sfx = [];
    this.claims = [];
    this.offBackend();
    this.backend.dispose();
    this.listeners.clear();
  }
}
