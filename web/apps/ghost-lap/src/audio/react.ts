// React bindings for the app's single AudioDirector.
import { useEffect, useRef, useSyncExternalStore } from "react";
import type { AudioMode, AudioStatus, ModeClaim } from "./AudioDirector.ts";
import { getAudio } from "./instance.ts";

export function useAudioStatus(): AudioStatus {
  const audio = getAudio();
  return useSyncExternalStore(audio.subscribe, audio.getStatus);
}

/**
 * Declare the sound a screen needs while it is mounted. The claim is released on unmount, which hands the sound back to the menu
 * (or to whichever claim is below). Pass "menu" while the screen is not yet in active play: the lobby and a finished run stay on gameLoading.
 */
export function useAudioMode(mode: AudioMode): void {
  const claim = useRef<ModeClaim | null>(null);
  useEffect(() => {
    const c = getAudio().claim(mode);
    claim.current = c;
    return () => { c.release(); claim.current = null; };
    // The claim is created once per mount; later mode changes go through `set` below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => { claim.current?.set(mode); }, [mode]);
}
