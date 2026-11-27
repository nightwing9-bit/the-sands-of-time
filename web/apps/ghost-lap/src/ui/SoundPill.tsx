// A quiet "Enable sound" affordance, shown only while the browser still blocks audio (no user gesture yet) and sound is not muted by choice.
import { getAudio } from "../audio/instance.ts";
import { useAudioStatus } from "../audio/react.ts";

export function SoundPill() {
  const { unlocked, settings } = useAudioStatus();
  if (unlocked || (settings.musicMuted && settings.sfxMuted)) return null;
  return (
    <button type="button" className="gl-status gl-sound-pill" data-tone="warn" onClick={() => void getAudio().unlock()} data-testid="enable-sound">
      Sound is off · click to enable
    </button>
  );
}
