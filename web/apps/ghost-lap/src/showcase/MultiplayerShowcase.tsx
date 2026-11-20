import { useEffect, useRef, useState } from "react";
import { UPDATE_PRIORITY, type Ticker } from "pixi.js";
import { getAudio } from "../audio/instance.ts";
import type { SfxKind } from "../audio/AudioDirector.ts";
import { CandleTextures } from "../candles/CandleAssets.ts";
import { CharacterTextures } from "../characters/CharacterAssets.ts";
import { SHAPE_BY_SLOT } from "../config/assetRegistry.ts";
import { slotLabel } from "../mp/model.ts";
import { PlayScene } from "../play/PlayScene.ts";
import { PlaySession } from "../play/PlaySession.ts";
import type { PlayEvent, PlayFrame, PlayTransport } from "../play/playTypes.ts";
import type { Vec } from "../play/visualSmoothing.ts";
import { Soulfire } from "../screens/PlayScreen.tsx";
import type { ArenaWorld } from "../stage/ArenaWorld.ts";
import { Stage } from "../stage/Stage.tsx";
import { WorldLayer } from "../stage/WorldLayer.tsx";
import { GameButton } from "../ui/primitives.tsx";
import { buildShowcase, SHOWCASE_HZ, SHOWCASE_MS } from "./choreography.ts";
import "./showcase.css";

const LABEL = "SCRIPTED MULTIPLAYER SHOWCASE · NOT LIVE";
const ROUND_TICKS = SHOWCASE_MS * SHOWCASE_HZ / 1000;
const FADE_MS = 150;
const NOOP = () => {};

class ShowcaseFeed implements PlayTransport {
  readonly label = LABEL;
  readonly production = false;
  private listener: (frame: PlayFrame) => void = NOOP;

  onFrame(listener: (frame: PlayFrame) => void) {
    this.listener = listener;
    return () => { this.listener = NOOP; };
  }
  onConnection() { return NOOP; }
  onInputAnswer() { return NOOP; }
  setIntent() {}
  async start() {}
  dispose() { this.listener = NOOP; }
  publish(frame: PlayFrame) { this.listener(frame); }
}

class ShowcaseSession extends PlaySession {
  private readonly positions = new Map<number, Vec>([0, 1, 2, 3].map((slot) => [slot, { x: 0, y: 0 }]));

  interpolate(frames: ReturnType<typeof buildShowcase>, tick: number) {
    const base = Math.floor(tick);
    const progress = tick - base;
    const a = frames[base]!.players;
    const b = frames[base + 1]!.players;
    for (const player of a) {
      const next = b[player.slot]!;
      const out = this.positions.get(player.slot)!;
      out.x = player.x + (next.x - player.x) * progress;
      out.y = player.y + (next.y - player.y) * progress;
    }
  }

  override render(): ReadonlyMap<number, Vec> { return this.positions; }
}

function eventSound(event: PlayEvent): SfxKind | undefined {
  if (event.kind === "CandleCollected") return "candle";
  if (event.kind === "ContactDrain" && event.value > 0) return "hit";
  if (event.kind === "BurstManual") return "burst";
}

function prepare(world: ArenaWorld, heads: CharacterTextures, candles: CandleTextures, cover: HTMLElement, setPlayers: (players: PlayFrame["players"]) => void) {
  const frames = buildShowcase();
  const feed = new ShowcaseFeed();
  const session = new ShowcaseSession(feed);
  const audio = getAudio();
  // The approved one-shots repeat with the action. This standalone capture has no backing track.
  const audioClaim = audio.claim("transition");
  const offAudio = session.onEvent((event, frame) => {
    const kind = eventSound(event);
    if (kind) audio.playEvent({ key: `${frame.identity}:${event.key}`, kind, origin: "living" });
  });

  let elapsed = 0;
  let lastFrameTime: number | null = null;
  let started = false;
  let loop = 0;
  let publishedTick = -1;

  function publish(tick: number) {
    const data = frames[tick]!;
    feed.publish({
      ...data, tick, identity: `showcase-${loop}`, receivedAtMs: tick * 1000 / SHOWCASE_HZ,
      tickRate: SHOWCASE_HZ, roundTicks: ROUND_TICKS, final: false,
      you: { slot: -1, canControl: false }, eventsComplete: true,
    });
    if (tick % 3 === 0 || data.events.length > 0) setPlayers(data.players);
  }

  function update(ticker: Ticker) {
    if (!started || document.hidden) {
      lastFrameTime = null;
      return;
    }
    // Pause across tab switches and ignore the first stale ticker delta after returning.
    if (lastFrameTime !== null) elapsed += Math.max(0, ticker.lastTime - lastFrameTime);
    lastFrameTime = ticker.lastTime;
    if (elapsed >= SHOWCASE_MS) {
      elapsed %= SHOWCASE_MS;
      loop++;
      publishedTick = -1;
      audio.resetEvents();
    }
    const tick = Math.min(ROUND_TICKS - 1e-7, elapsed * SHOWCASE_HZ / 1000);
    session.interpolate(frames, tick);
    // Feed the state and cues for every crossed simulation tick before Pixi draws.
    while (publishedTick < Math.floor(tick)) publish(++publishedTick);
    const edge = Math.min(loop ? elapsed : SHOWCASE_MS, SHOWCASE_MS - elapsed);
    cover.style.opacity = String(Math.max(0, 1 - edge / FADE_MS));
  }

  const resetFrameClock = () => { lastFrameTime = null; };
  document.addEventListener("visibilitychange", resetFrameClock);
  world.ticker.add(update, undefined, UPDATE_PRIORITY.HIGH);
  const scene = new PlayScene(world, heads, session, candles, {
    overlappingBursts: true, visualTime: () => elapsed,
  });
  session.interpolate(frames, 0);
  publish(++publishedTick);

  return {
    play() {
      if (started) return;
      started = true;
      void audio.unlock().catch((error: unknown) => console.warn("Showcase audio unavailable", error));
    },
    dispose() {
      document.removeEventListener("visibilitychange", resetFrameClock);
      world.ticker.remove(update);
      offAudio();
      audio.resetEvents();
      audioClaim.release();
      scene.dispose();
      session.dispose();
    },
  };
}

export function MultiplayerShowcase() {
  const [world, setWorld] = useState<ArenaWorld | null>(null);
  const [ready, setReady] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [error, setError] = useState("");
  const [players, setPlayers] = useState<PlayFrame["players"]>([]);
  const root = useRef<HTMLDivElement>(null);
  const cover = useRef<HTMLDivElement>(null);
  const run = useRef<ReturnType<typeof prepare> | null>(null);

  useEffect(() => {
    if (!world) return;
    const abort = new AbortController();
    let heads: CharacterTextures | undefined;
    let candles: CandleTextures | undefined;
    const load = async () => {
      try {
        await Promise.all([
          CharacterTextures.load(abort.signal).then((asset) => { if (abort.signal.aborted) asset.destroy(); else heads = asset; }),
          CandleTextures.load(abort.signal).then((asset) => { if (abort.signal.aborted) asset.destroy(); else candles = asset; }),
        ]);
        if (abort.signal.aborted) return;
        run.current = prepare(world, heads!, candles!, cover.current!, setPlayers);
        setReady(true);
      } catch (cause) {
        if (!abort.signal.aborted) setError(String(cause));
      }
    };
    void load();
    return () => {
      abort.abort();
      run.current?.dispose();
      run.current = null;
      heads?.destroy();
      candles?.destroy();
    };
  }, [world]);

  function play() {
    run.current?.play();
    setPlaying(true);
    if (document.fullscreenEnabled) void root.current?.requestFullscreen().catch(NOOP);
  }

  return (
    <div ref={root} className="gl-showcase">
      <Stage scene="day" world={<WorldLayer scene="day" walls onWorld={setWorld} />}
        hud={
          <div className="gl-play gl-mp-hud">
            <span className="gl-showcase-label">{LABEL}</span>
            <ul className="gl-play-rail gl-mp-rail" aria-label="Players">
              {players.map((p) => (
                <li key={p.slot}>
                  <img src={SHAPE_BY_SLOT[p.slot]?.file} alt="" width={22} height={22} data-slot={p.slot} />
                  <span className="gl-mp-rail-name">{slotLabel(p.slot)}</span>
                  <b>{p.totalScore}</b>
                  <Soulfire charge={p.charge} />
                </li>
              ))}
            </ul>
          </div>
        }
        overlay={
          <>
            <div ref={cover} className="gl-showcase-cover" aria-hidden="true" />
            {!playing && <div className="gl-showcase-start">
              {error ? <p role="alert">{error}</p> : (
                <GameButton variant="primary" onClick={play} unavailable={ready ? undefined : "Loading the arena"}>Play showcase</GameButton>
              )}
            </div>}
          </>
        }
      />
    </div>
  );
}
