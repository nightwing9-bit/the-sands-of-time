// PixiJS greybox arena (spec 06, task 6.5). Placeholder rectangles and circles only (final art is Spec 13). It draws exactly the
// server's world: walls from the arena descriptor, candles and players from the latest verified/committed state. It never moves a
// sprite on its own, so nothing on screen can earn score. React owns the HUD; Pixi renders only the arena (skills2 §11.3).
import { useEffect, useRef, useState } from "react";
import { Application, Graphics } from "pixi.js";
import type { Arena, GhostView, WorldView } from "./types.ts";

const COLORS = {
  arena: 0x12151f,
  wallTop: 0x3a3f55,
  wallRim: 0x5b6285,
  flame: 0xffb347,
  flameCore: 0xffe9a8,
  telegraph: 0x8a88a0,
  ghost: 0x7fe8ff,
  alternate: 0xffc857,
  soulfire: 0xffd24a,
};

export interface ArenaViewProps {
  arena: Arena;
  world: WorldView | null;
  ghost?: GhostView | null;
  /** "original": the one player is the recorded run (cyan). "living": a playable world (gold). */
  playerRole: "original" | "living";
}

interface Scene {
  arena: Arena;
  world: WorldView | null;
  ghost: GhostView | null;
  role: "original" | "living";
  burstUntil: number;
  lastBursts: number;
}

function drawStatic(g: Graphics, arena: Arena) {
  g.rect(0, 0, arena.width, arena.height).fill({ color: COLORS.arena });
  for (const o of arena.obstacles) {
    g.rect(o.x0, o.y0, o.x1 - o.x0, o.y1 - o.y0).fill({ color: COLORS.wallTop }).stroke({ width: 3, color: COLORS.wallRim });
  }
}

function drawDynamic(g: Graphics, s: Scene, now: number) {
  g.clear();
  const { arena, world, ghost } = s;
  if (!world) return;
  for (const c of world.telegraphCandles) {
    g.circle(c.x, c.y, arena.candleHalf + 6).stroke({ width: 2, color: COLORS.telegraph, alpha: 0.8 });
  }
  for (const c of world.activeCandles) {
    const h = arena.candleHalf;
    g.rect(c.x - h, c.y - h, h * 2, h * 2).fill({ color: COLORS.flame });
    g.circle(c.x, c.y, h / 2).fill({ color: COLORS.flameCore });
  }
  const half = arena.colliderHalf;
  // Z-order: floor -> ghost -> living player. The ghost keeps a rim halo so both read even when fully overlapped.
  if (ghost) {
    g.rect(ghost.x - half, ghost.y - half, half * 2, half * 2).fill({ color: COLORS.ghost, alpha: 0.55 });
    g.circle(ghost.x, ghost.y, half * 1.12 + 6).stroke({ width: 3, color: COLORS.ghost, alpha: 0.9 });
  }
  const p = world.players[0];
  if (p) {
    if (now < s.burstUntil) g.circle(p.x, p.y, arena.burstRadius).stroke({ width: 4, color: COLORS.soulfire, alpha: 0.85 });
    const color = s.role === "original" ? COLORS.ghost : COLORS.alternate;
    g.rect(p.x - half, p.y - half, half * 2, half * 2).fill({ color }).stroke({ width: 3, color: 0x0b0c16 });
    // Soulfire sockets: four pips above the player, filled for each charge stage; a ring when Overlit.
    for (let i = 0; i < arena.maxCharge; i++) {
      g.circle(p.x - 21 + i * 14, p.y - half - 12, 5).fill({ color: i < p.charge ? COLORS.soulfire : 0x232738 });
    }
    if (p.overlit) g.circle(p.x, p.y, half + 10).stroke({ width: 4, color: COLORS.soulfire });
  }
}

export function ArenaView({ arena, world, ghost = null, playerRole }: ArenaViewProps) {
  const host = useRef<HTMLDivElement | null>(null);
  const scene = useRef<Scene>({ arena, world, ghost, role: playerRole, burstUntil: 0, lastBursts: world?.players[0]?.bursts ?? 0 });
  const [failed, setFailed] = useState<string | null>(null);

  // Burst rings react to the server's Burst counter/events, never to a key press.
  const s = scene.current;
  const bursts = world?.players[0]?.bursts ?? 0;
  if (bursts > s.lastBursts || world?.events.some((e) => e.kind === "BurstManual" || e.kind === "BurstAuto")) s.burstUntil = performance.now() + 350;
  s.lastBursts = bursts;
  s.arena = arena;
  s.world = world;
  s.ghost = ghost;
  s.role = playerRole;

  useEffect(() => {
    let cancelled = false;
    let app: Application | null = new Application();
    const created = app;
    void created
      .init({ width: arena.width, height: arena.height, background: COLORS.arena, antialias: true, autoDensity: true, resolution: Math.min(window.devicePixelRatio || 1, 2) })
      .then(() => {
        if (cancelled || !host.current) return created.destroy(true);
        created.canvas.style.width = "100%";
        created.canvas.style.height = "100%";
        created.canvas.setAttribute("aria-hidden", "true");
        host.current.appendChild(created.canvas);
        const floor = new Graphics();
        drawStatic(floor, arena);
        const dynamic = new Graphics();
        created.stage.addChild(floor, dynamic);
        created.ticker.add(() => drawDynamic(dynamic, scene.current, performance.now()));
      })
      .catch((error: unknown) => {
        if (!cancelled) setFailed(error instanceof Error ? error.message : "unknown error");
      });
    return () => {
      cancelled = true;
      if (app && created.renderer) created.destroy(true);
      app = null;
    };
  }, [arena]);

  return (
    <div className="arena" data-testid="arena" data-renderer={failed ? "failed" : "pixi"}>
      <div ref={host} className="arena-canvas" />
      {failed ? <p role="alert" data-testid="arena-error">The arena renderer could not start ({failed}). Scores and state below are still the server's.</p> : null}
    </div>
  );
}
