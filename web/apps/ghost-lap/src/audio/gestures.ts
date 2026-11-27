// Browser-side wiring of the AudioDirector: audio unlock on a REAL user gesture, menu.wav on a REAL UI activation, and resume after the tab returns.
// Kept apart from the director so the director stays DOM-free.
import type { AudioDirector } from "./AudioDirector.ts";

/**
 * Controls that are development tooling, not game UI. A click here is not a "real UI activation" for menu.wav: it would bury the very sounds the
 * listening study is meant to judge. Marked by class (preview strip, dev panels) or `data-audio="silent"`.
 */
export const SILENT_UI = '[data-audio="silent"], .gl-preview-strip, .gl-play-dev, .gl-ns-panel';
const ACTIVATABLE = 'button, a[href], [role="button"], input[type="range"], input[type="checkbox"]';

export function isRealUiActivation(target: EventTarget | null, trusted: boolean): boolean {
  if (!trusted || !(target instanceof Element)) return false;
  const control = target.closest(ACTIVATABLE);
  if (!control) return false;
  if (control.closest(SILENT_UI)) return false;
  if (control.getAttribute("aria-disabled") === "true" || (control as HTMLButtonElement).disabled) return false;
  // Range sliders fire click on every drag end; they are adjustments, not activations.
  if (control instanceof HTMLInputElement && control.type === "range") return false;
  return true;
}

/** Returns an uninstall function (used by HMR and tests). */
export function installAudioGestures(audio: AudioDirector, doc: Document = document): () => void {
  let unlocked = false;
  const tryUnlock = (event: Event) => {
    if (!event.isTrusted || unlocked) return;
    void audio.unlock().then((ok) => { unlocked = unlocked || ok; });
  };
  // pointerdown and keydown count as user activation in browsers; click covers assistive technology that only dispatches click.
  const gestureEvents = ["pointerdown", "keydown", "click"] as const;
  for (const name of gestureEvents) doc.addEventListener(name, tryUnlock, true);

  const onClick = (event: MouseEvent) => {
    if (isRealUiActivation(event.target, event.isTrusted)) audio.playUiClick();
  };
  doc.addEventListener("click", onClick, true);

  // A returning tab may find the context suspended or interrupted (Safari). Resume sound; this never touches game state or pauses anything.
  const onVisible = () => { if (doc.visibilityState === "visible" && unlocked) void audio.unlock(); };
  doc.addEventListener("visibilitychange", onVisible);

  return () => {
    for (const name of gestureEvents) doc.removeEventListener(name, tryUnlock, true);
    doc.removeEventListener("click", onClick, true);
    doc.removeEventListener("visibilitychange", onVisible);
  };
}
