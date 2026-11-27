// The persistent 16:9 stage. AppShell renders ONE Stage for the whole session; screens only supply what goes in its layers:
//   world   the single world mount (13.1: the static snow image; 13.2: the one long-lived PixiJS application)
//   hud     DOM HUD and screen furniture, anchored to --gl-hud-* rails of this same rectangle
//   overlay panels and notices above the HUD
//   dock    optional bottom dock (history). The dock lives OUTSIDE the stage rectangle: the full 16:9 stage re-fits into the remaining
//           height, so a wall at the bottom of the arena is never covered.
// Layout is container-query based, so every size inside the stage follows the stage, not the browser window.
import type { ReactNode } from "react";
import type { SceneMode } from "../presentation/types.ts";

export interface StageProps {
  scene: SceneMode;
  world: ReactNode;
  hud?: ReactNode;
  overlay?: ReactNode;
  dock?: ReactNode;
  showSafeGuides?: boolean;
}

export function Stage({ scene, world, hud, overlay, dock, showSafeGuides = false }: StageProps) {
  return (
    <div className="gl-viewport" data-scene={scene} data-testid="stage-viewport">
      <div className="gl-slot">
        <div className="gl-stage" data-testid="stage" role="presentation">
          <div className="gl-world">{world}</div>
          <div className="gl-hud">{hud}</div>
          <div className="gl-overlay">{overlay}</div>
          {showSafeGuides ? (
            <div className="gl-safe-guides" aria-hidden="true" data-testid="safe-guides">
              <div className="top"><span>HUD rail (top)</span></div>
              <div className="play"><span>Play area: keep clear</span></div>
              <div className="bottom"><span>HUD tray (bottom)</span></div>
            </div>
          ) : null}
        </div>
      </div>
      {dock}
    </div>
  );
}
