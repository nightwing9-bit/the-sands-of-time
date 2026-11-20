// PREVIEW FIXTURE: the physical relationship study of the signature interface. A hand-set tick (1140 of 2700, the 0:38 moment) drives a
// static hourglass and track, to judge placement, scale and material. Nothing here is loaded history, nothing seeks, and nothing forks.
// The real dock (13.10) wraps the shared @sands/timeline-ui scrubber in this same frame: hourglass left, track right, source chip, time.
import { FrostPanel, BackLink } from "../ui/primitives.tsx";
import { HourglassMark } from "../ui/HourglassMark.tsx";

const FIXTURE = { tick: 1140, finalTick: 2700, rate: 30 } as const;
const clock = (tick: number) => `${Math.floor(tick / FIXTURE.rate / 60)}:${String(Math.floor(tick / FIXTURE.rate) % 60).padStart(2, "0")}`;
const elapsed = FIXTURE.tick / FIXTURE.finalTick;
const TENS = Array.from({ length: 8 }, (_, i) => ((i + 1) * 300) / FIXTURE.finalTick); // a tick every 10 seconds of the 90-second run

export function HistoryStudyHud({ onBack }: { onBack: () => void }) {
  return (
    <>
      <div className="gl-study-top">
        <BackLink onClick={onBack}>Home</BackLink>
        <span className="gl-fixture-tag" data-testid="fixture-tag">Preview fixture · not a saved history</span>
      </div>
    </>
  );
}

export function HistoryDockStudy() {
  const percent = `${(elapsed * 100).toFixed(3)}%`;
  return (
    <FrostPanel className="gl-dock" aria-label="History dock study (preview fixture)" data-testid="history-dock">
      <div className="gl-dock-hourglass" data-testid="dock-hourglass">
        <HourglassMark elapsed={elapsed} label="Hourglass: 42 percent of the run has run out" />
      </div>
      <div className="gl-dock-main">
        <div className="gl-dock-top">
          <span className="gl-chip"><i aria-hidden="true" />Original run · fixture</span>
          <output className="gl-dock-time" data-testid="dock-time">
            {clock(FIXTURE.tick)} / {clock(FIXTURE.finalTick)}<span>T{FIXTURE.tick} of {FIXTURE.finalTick}</span>
          </output>
        </div>
        <div className="gl-track" role="img" aria-label={`Timeline, fixture position ${clock(FIXTURE.tick)}, left is earlier`} data-testid="dock-track">
          <span className="gl-track-rest" />
          <span className="gl-track-played" style={{ width: percent }} />
          {TENS.map((p) => <span key={p} className="gl-track-tick" style={{ left: `${p * 100}%` }} />)}
          <span className="gl-playhead" style={{ left: percent }} />
        </div>
        <div className="gl-dock-scale" aria-hidden="true"><span>0:00</span><span>0:30</span><span>1:00</span><span>1:30</span></div>
      </div>
    </FrostPanel>
  );
}
