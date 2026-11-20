// Music and Effects volume + mute (R13.35 Settings, task 13.7). One component for the Settings screen and the dev sound study; both read and write the
// one AudioDirector. Night ambience follows the Music controls (no third slider, by design).
import { getAudio } from "../audio/instance.ts";
import { useAudioStatus } from "../audio/react.ts";

interface RowProps { readonly label: string; readonly testId: string; readonly volume: number; readonly muted: boolean; readonly onVolume: (v: number) => void; readonly onMute: (muted: boolean) => void }

function Row({ label, testId, volume, muted, onVolume, onMute }: RowProps) {
  const id = `gl-vol-${testId}`;
  return (
    <div className="gl-audio-row">
      <label htmlFor={id}>{label}</label>
      <input id={id} type="range" min={0} max={100} step={1} value={Math.round(volume * 100)} data-testid={`${testId}-volume`}
        aria-valuetext={`${Math.round(volume * 100)} percent${muted ? ", muted" : ""}`}
        onChange={(e) => onVolume(Number(e.currentTarget.value) / 100)} />
      <output htmlFor={id}>{Math.round(volume * 100)}</output>
      <button type="button" className="gl-audio-mute" aria-pressed={muted} onClick={() => onMute(!muted)} data-testid={`${testId}-mute`}>{muted ? "Muted" : "Mute"}</button>
    </div>
  );
}

export function AudioControls() {
  const { settings, unlocked } = useAudioStatus();
  const audio = getAudio();
  return (
    <div className="gl-audio-controls" data-testid="audio-controls">
      <Row label="Music" testId="music" volume={settings.musicVolume} muted={settings.musicMuted} onVolume={(v) => audio.setMusicVolume(v)} onMute={(m) => audio.setMusicMuted(m)} />
      <Row label="Effects" testId="sfx" volume={settings.sfxVolume} muted={settings.sfxMuted} onVolume={(v) => audio.setSfxVolume(v)} onMute={(m) => audio.setSfxMuted(m)} />
      <p className="gl-audio-note" data-testid="audio-lock-note">
        {unlocked ? "Sound is on." : "Sound starts after your first click or key press in this window (a browser rule)."}
      </p>
    </div>
  );
}
