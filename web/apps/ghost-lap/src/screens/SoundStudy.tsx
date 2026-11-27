// PREVIEW (development only): the 13.7 listening path. NOT gameplay.
//
// Everything audible here goes through the REAL AudioDirector: the same mode claims, the same night ambience sequencer, the same event de-duplication
// and the same buses the game uses. What is a fixture: the buttons themselves. A button stands in for the confirmed server event or the screen that
// would claim the mode; real play (Day play screen) produces the same calls from real events. The panel says so and never calls these "game events".
//   REAL      the nine approved files, buses, fades, volume/mute, wind-wind-knock completion sequencing, event de-duplication, night gate.
//   FIXTURE   who presses the button (you), event keys ("study-N"), the one-click "Day to Night" that waits 1.5 s in place of a real transition + ready gate.
import { useEffect, useRef, useState } from "react";
import { BackLink } from "../ui/primitives.tsx";
import { AudioControls } from "../ui/AudioControls.tsx";
import { getAudio } from "../audio/instance.ts";
import { useAudioMode, useAudioStatus } from "../audio/react.ts";
import { AMBIENCE_SEQUENCE, CUES, CUE_IDS, MUSIC_FOR_MODE, type AudioMode, type SfxKind } from "../audio/cues.ts";

const MODE_LABEL: Record<AudioMode, string> = { menu: "Menu / lobby", day: "Day play", transition: "Preparing / transition", night: "Night gameplay ready" };
const file = (id: keyof typeof CUES): string => CUES[id].url.split("/").pop() ?? id;
const STEP_LABEL = ["wind 1 of 2", "wind 2 of 2", "knock"];

export function SoundStudyHud({ onBack }: { onBack: () => void }) {
  const [mode, setMode] = useState<AudioMode>("menu");
  const [result, setResult] = useState("Nothing played yet.");
  const status = useAudioStatus();
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const seq = useRef(0);
  const sent = useRef<{ key: string; kind: SfxKind } | null>(null);
  const keys = useRef(new Set<string>());
  useAudioMode(mode);

  useEffect(() => {
    const id = setInterval(() => getAudio().refresh(), 500); // live voices change without an event of ours
    return () => { clearInterval(id); if (timer.current) clearTimeout(timer.current); };
  }, []);

  const choose = (next: AudioMode) => { if (timer.current) clearTimeout(timer.current); setMode(next); };
  const dayToNight = () => {
    if (timer.current) clearTimeout(timer.current);
    setMode("transition");
    timer.current = setTimeout(() => setMode("night"), 1500);
  };
  const send = (kind: SfxKind, origin: "living" | "ghost" = "living", again = false) => {
    const key = again && sent.current ? sent.current.key : `study-${++seq.current}`;
    const kindToSend = again && sent.current ? sent.current.kind : kind;
    const dup = keys.current.has(`${kindToSend}:${key}`);
    keys.current.add(`${kindToSend}:${key}`);
    if (origin === "living") sent.current = { key, kind: kindToSend };
    const played = getAudio().playEvent({ key, kind: kindToSend, origin });
    setResult(`${kindToSend} · key ${key}${origin === "ghost" ? " · GHOST origin" : ""} → ${played ? "SOUNDED" : origin === "ghost" ? "silent: ghost cues never sound" : dup ? "silent: duplicate key (once per event)" : "silent: sound locked or muted"}`);
  };

  const wantedMusic = MUSIC_FOR_MODE[status.mode];
  const refused = status.nightBlocked;
  return (
    <div className="gl-ss" data-testid="sound-study" data-unlocked={status.unlocked ? "1" : "0"} data-mode={status.mode} data-music={status.musicCue ?? "none"} data-ambience={status.ambienceStep ?? "none"}>
      <div className="gl-study-top">
        <BackLink onClick={onBack}>Home</BackLink>
        <span className="gl-fixture-tag" data-testid="fixture-tag">PREVIEW FIXTURE · button-driven audition of the real audio path · not live play · mix is HUMAN REVIEW PENDING</span>
      </div>
      <div className="gl-ss-panel" data-audio="silent" role="region" aria-label="Sound study controls">
        <output className="gl-ss-readout" data-testid="ss-readout">
          <b>{status.unlocked ? "Sound on" : "Sound locked: click any button once"}</b>
          <span>mode {MODE_LABEL[status.mode]}{refused ? " · night REFUSED (needs a transition first), staying quiet" : ""}</span>
          <span>music {status.musicCue ? file(status.musicCue) : wantedMusic && !status.unlocked ? `${file(wantedMusic)} (waiting for unlock)` : "none"}</span>
          <span>ambience {status.ambienceCue ? file(status.ambienceCue) : "none"} · finished segments {status.ambienceCompleted} · live effects {status.liveSfx}</span>
        </output>

        <div role="group" aria-label="Mood">
          <span className="gl-cs-k">Mood</span>
          {(["menu", "day", "transition", "night"] as const).map((m) => (
            <button key={m} type="button" aria-pressed={mode === m} onClick={() => choose(m)} data-testid={`ss-mode-${m}`}>{MODE_LABEL[m]}</button>
          ))}
          <button type="button" onClick={dayToNight} data-testid="ss-day-to-night">Day → Night (transition, then ready)</button>
        </div>
        <p className="gl-cs-note">Menu loops gameLoading.wav. Day loops multiplayer_theme.wav. Transition is quiet by design. Night starts ghost_main_theme.wav and the wind, wind, knock cycle together, but only straight after a transition: pressing Night from Menu or Day is refused.</p>

        <div role="group" aria-label="Night ambience">
          <span className="gl-cs-k">Night</span>
          <ol className="gl-ss-steps" data-testid="ss-steps">
            {AMBIENCE_SEQUENCE.map((cue, i) => (
              <li key={i} data-active={status.ambienceStep === i ? "1" : "0"}>{STEP_LABEL[i]} <small>{file(cue)}</small></li>
            ))}
          </ol>
          <button type="button" disabled={status.ambienceStep === null} onClick={() => getAudio().skipAmbienceStep()} data-testid="ss-skip">Skip to next segment (dev)</button>
        </div>
        <p className="gl-cs-note">Each segment starts when the previous one actually finishes (wind 8.98 s, wind 8.98 s, knock 3.87 s, about 22 s a cycle). "Skip" cuts the current one short so you can reach the knock quickly.</p>

        <div role="group" aria-label="Effects">
          <span className="gl-cs-k">Effects</span>
          <button type="button" onClick={() => send("candle")} data-testid="ss-candle">Candle collected</button>
          <button type="button" onClick={() => send("hit")} data-testid="ss-hit">Weaker contact (hit)</button>
          <button type="button" onClick={() => send("burst")} data-testid="ss-burst">Burst</button>
          <button type="button" onClick={() => send("candle", "living", true)} data-testid="ss-repeat">Same event again</button>
          <button type="button" onClick={() => send("candle", "ghost")} data-testid="ss-ghost">Ghost candle</button>
          <button type="button" onClick={() => { const ok = getAudio().playUiClick(); setResult(`UI click (menu.wav) → ${ok ? "SOUNDED" : "silent: throttled, locked or muted"}`); }} data-testid="ss-click">UI click</button>
        </div>
        <p className="gl-cs-note" data-testid="ss-result" aria-live="polite">{result}</p>

        <div role="group" aria-label="Volume">
          <span className="gl-cs-k">Volume</span>
          <AudioControls />
        </div>

        <details className="gl-ss-trims">
          <summary>Per-sound level trims (dB, session only): report the numbers you like</summary>
          {CUE_IDS.map((id) => (
            <label key={id} className="gl-ss-trim">
              <span>{file(id)} <small>{CUES[id].role}</small></span>
              <input type="range" min={-24} max={12} step={0.5} value={status.trimDb[id]} onChange={(e) => getAudio().setCueTrim(id, Number(e.currentTarget.value))} data-testid={`ss-trim-${id}`} />
              <output>{status.trimDb[id] > 0 ? "+" : ""}{status.trimDb[id]} dB</output>
            </label>
          ))}
          <button type="button" onClick={() => getAudio().resetCueTrims()}>Reset to first-pass values</button>
        </details>

        <p className="gl-cs-note" data-testid="ss-cues">
          {CUE_IDS.map((id) => `${file(id)}: ${status.cueState[id]}, started ${status.started[id]}×`).join(" · ")}
        </p>
      </div>
    </div>
  );
}
