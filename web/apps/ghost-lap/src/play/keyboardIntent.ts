// Keyboard -> ControlIntent. WASD or arrows move, Space is Burst. This module owns only "which keys are down"; it knows no network.
//
// Burst contract (13.0 map A3): the wire carries requestBurst as a HELD pressed state and the C# reducer detects the rising edge. So the
// press and the release are both real, separate changes and BOTH are reported, never merged into "no change". A transport must therefore
// deliver a pressed intent even if the key is already up again by the time it sends.
//
// Neutral rule: blur, hidden tab, a lost window, dispose, or an explicit reset releases every key and reports a neutral intent.
import type { ControlIntent } from "../presentation/types.ts";

const MOVE_KEYS: Readonly<Record<string, "up" | "down" | "left" | "right">> = {
  ArrowUp: "up", KeyW: "up", ArrowDown: "down", KeyS: "down", ArrowLeft: "left", KeyA: "left", ArrowRight: "right", KeyD: "right",
};
const BURST_CODE = "Space";

export const NEUTRAL: ControlIntent = { moveX: 0, moveY: 0, requestBurst: false };

export function sameIntent(a: ControlIntent, b: ControlIntent): boolean {
  return a.moveX === b.moveX && a.moveY === b.moveY && a.requestBurst === b.requestBurst;
}

/** Pure key bookkeeping (no DOM): testable. Opposite directions cancel, exactly like the greybox precedent. */
export class KeyState {
  private readonly down = new Set<string>();
  private burst = false;
  /** Returns true when the code is a game key (the caller then prevents the browser default). */
  press(code: string): boolean {
    if (code === BURST_CODE) { this.burst = true; return true; }
    const dir = MOVE_KEYS[code];
    if (!dir) return false;
    this.down.add(code);
    return true;
  }
  release(code: string): boolean {
    if (code === BURST_CODE) { const was = this.burst; this.burst = false; return was; }
    return this.down.delete(code);
  }
  clear(): void {
    this.down.clear();
    this.burst = false;
  }
  intent(): ControlIntent {
    let x = 0, y = 0;
    const dirs = new Set<string>();
    for (const code of this.down) { const d = MOVE_KEYS[code]; if (d) dirs.add(d); }
    if (dirs.has("right")) x += 1;
    if (dirs.has("left")) x -= 1;
    if (dirs.has("down")) y += 1;
    if (dirs.has("up")) y -= 1;
    return { moveX: x as -1 | 0 | 1, moveY: y as -1 | 0 | 1, requestBurst: this.burst };
  }
}

export interface KeyboardBinding { reset(): void; dispose(): void }

/**
 * Binds window keyboard + focus events. `onChange` fires for every real intent change (press and release of Burst included) and with
 * reason "neutral" when focus/visibility forced a release. Repeats are ignored: holding a key is one press. `enabled()` is read on each
 * event so a spectator or superseded tab (CanControl=false) never produces input.
 */
export function bindKeyboard(
  onChange: (intent: ControlIntent, reason: "change" | "neutral") => void,
  enabled: () => boolean = () => true,
  target: Window = window,
): KeyboardBinding {
  const keys = new KeyState();
  let last: ControlIntent = NEUTRAL;
  const emit = (reason: "change" | "neutral") => {
    const next = keys.intent();
    if (reason === "change" && sameIntent(next, last)) return;
    last = next;
    onChange(next, reason);
  };
  const forceNeutral = () => {
    const had = !sameIntent(last, NEUTRAL);
    keys.clear();
    // Always report once when something was held (and once more is harmless to the server: neutral is idempotent).
    if (had) emit("neutral");
  };
  const typing = (event: KeyboardEvent) => event.target instanceof HTMLElement && (event.target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName));
  const onDown = (event: KeyboardEvent) => {
    if (event.ctrlKey || event.metaKey || event.altKey || typing(event)) return;
    if (!enabled()) return;
    if (!keys.press(event.code)) return;
    event.preventDefault(); // Space would scroll, arrows would scroll
    if (event.repeat) return;
    emit("change");
  };
  const onUp = (event: KeyboardEvent) => {
    if (keys.release(event.code)) emit("change");
  };
  const onVisibility = () => { if (document.visibilityState === "hidden") forceNeutral(); };
  target.addEventListener("keydown", onDown);
  target.addEventListener("keyup", onUp);
  target.addEventListener("blur", forceNeutral);
  document.addEventListener("visibilitychange", onVisibility);
  return {
    reset: forceNeutral,
    dispose() {
      target.removeEventListener("keydown", onDown);
      target.removeEventListener("keyup", onUp);
      target.removeEventListener("blur", forceNeutral);
      document.removeEventListener("visibilitychange", onVisibility);
      forceNeutral();
    },
  };
}
