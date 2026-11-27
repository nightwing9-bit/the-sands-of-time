// The one AudioDirector of the running app, created on first use and kept for the page's lifetime (never per screen). Hot reload disposes the old
// instance so a dev edit cannot leave a second theme playing under the new one.
import { AudioDirector, type StorageLike } from "./AudioDirector.ts";
import { installAudioGestures } from "./gestures.ts";
import { WebAudioBackend } from "./webAudioBackend.ts";

let director: AudioDirector | null = null;
let uninstall: (() => void) | null = null;

function safeStorage(): StorageLike | null {
  try { return window.localStorage; } catch { return null; }
}

export function getAudio(): AudioDirector {
  if (!director) {
    director = new AudioDirector(new WebAudioBackend(), { storage: safeStorage() });
    uninstall = installAudioGestures(director);
  }
  return director;
}

if (import.meta.hot) {
  import.meta.hot.dispose(() => {
    uninstall?.();
    director?.dispose();
    director = null;
    uninstall = null;
  });
}
