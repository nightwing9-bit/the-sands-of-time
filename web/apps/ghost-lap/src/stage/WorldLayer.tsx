// The world mount of the persistent stage: one long-lived PixiJS application (ArenaWorld) created once for the stage's lifetime.
// Scenes and screens only change what is drawn in it (walls on/off, geometry overlay), never the canvas.
//
// Preview geometry is the source-checked descriptor. MultiplayerTransport refuses play unless the production GetArena projection
// matches it and its layout fingerprint matches the sealed round. The renderer and authority therefore use the same nine rectangles.
import { useEffect, useRef, useState } from "react";
import { ArenaWorld } from "./ArenaWorld.ts";
import { ARENA_V1_RENDER } from "../arena/arenaV1Descriptor.ts";
import type { SceneMode } from "../presentation/types.ts";

export function WorldLayer({ scene, walls = false, geometryOverlay = false, onWorld }: { scene: SceneMode; walls?: boolean; geometryOverlay?: boolean; onWorld?: (world: ArenaWorld | null) => void }) {
  const host = useRef<HTMLDivElement>(null);
  const [world, setWorld] = useState<ArenaWorld | null>(null);
  const [failed, setFailed] = useState(false);
  // Screens that draw into the shared actors layer (heads, later candles) receive the one long-lived world through this callback.
  const onWorldRef = useRef(onWorld);
  onWorldRef.current = onWorld;

  useEffect(() => {
    const abort = new AbortController();
    let created: ArenaWorld | null = null;
    ArenaWorld.create(host.current!, ARENA_V1_RENDER, abort.signal).then(
      (w) => {
        if (abort.signal.aborted) return w.destroy();
        created = w;
        setWorld(w);
        onWorldRef.current?.(w);
      },
      (error: unknown) => { if (!abort.signal.aborted) { console.error("Arena failed to start", error); setFailed(true); } },
    );
    return () => {
      abort.abort();
      onWorldRef.current?.(null);
      created?.destroy();
      setWorld(null);
    };
  }, []);

  // Only the night SCENE grades the environment (never live multiplayer or solo Original). A study may also call world.setEnvironment itself.
  useEffect(() => { world?.setEnvironment(scene === "night" ? "night" : "day"); }, [world, scene]);
  useEffect(() => { world?.setWalls(walls ? ARENA_V1_RENDER.obstacles : null); }, [world, walls]);
  useEffect(() => { world?.setGeometryOverlay(geometryOverlay && walls, ARENA_V1_RENDER.obstacles); }, [world, walls, geometryOverlay]);

  return (
    <div ref={host} className="gl-pixi-host" data-testid="world-layer" data-scene={scene} data-ready={world ? "true" : "false"} data-walls={walls ? "true" : "false"}>
      {failed ? <p className="gl-world-failed" role="alert">The arena could not start (WebGL unavailable).</p> : null}
    </div>
  );
}
