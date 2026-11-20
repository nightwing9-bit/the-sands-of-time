# 09 — Implementation architecture, libraries, commands and render adapters

**Do not set up a second application**. First locate current frontend root via actual `package.json`/workspace; identify package manager (`package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `bun.lock*`), React version, and current routing/styling conventions. This file gives a suggested module layout; adapt it to existing repo, document deviations, but preserve responsibilities.

## 1. Runtime dependencies — verified library pairings

PixiJS v8 + community `pixi-filters` v6 + PixiJS Sound v6 are documented compatible pairs. Commands **assuming npm frontend repo** (translate to existing package manager; avoid duplicate install):

```bash
# RUN ONLY INSIDE VERIFIED FRONTEND PACKAGE ROOT
npm install pixi.js@^8 pixi-filters@^6 @pixi/sound@^6
npm install @fontsource/source-serif-4 @fontsource/source-sans-3
# OPTIONAL reference material for coding agents, not a runtime package:
npx skills add https://github.com/pixijs/pixijs-skills
```

- If existing dependencies are already version-compatible, preserve lockfile and packages; don't force a React upgrade.
- `@pixi/react` is **not needed** for this design. If existing app uses React 18, do **not** install current `@pixi/react` v8 (documents React 19 peer requirement) just to draw a game canvas; create a vanilla PixiJS v8 scene inside a React-owned mounting div with one `useEffect` lifecycle.
- No GSAP required: visual timing is small and can use CSS transitions for UI plus Pixi ticker for scene. Do **not** install a particle library without a confirmed visual feature that needs it.
- Fontsource packages install WOFF2/CSS for self-hosted fonts; include appropriate OFL licences in deployed source.

**Exact public API direction:**

```ts
import { Application, Assets, Sprite, AnimatedSprite,
         Container, ColorMatrixFilter, Filter } from 'pixi.js';
import { ShockwaveFilter } from 'pixi-filters/shockwave';
import { CRTFilter } from 'pixi-filters/crt';
// If approved for chosen lightmap pipeline:
import { SimpleLightmapFilter } from 'pixi-filters/simple-lightmap';
import { sound } from '@pixi/sound';

// CSS import where your app loads styles:
import '@fontsource/source-serif-4/400.css';
import '@fontsource/source-serif-4/500.css';
import '@fontsource/source-sans-3/400.css';
import '@fontsource/source-sans-3/500.css';
import '@fontsource/source-sans-3/600.css';
```

After installation, **verify the actual available exports and options against pinned package docs and TS compiler**; none of the above are permission to blindly copy deprecated v7 filter patterns. Do not install deprecated individual `@pixi/filter-*` packages. Avoid an `@pixi/particle-emitter` dependency unless new approved particle effects truly require it.

**Official references:**
- PixiJS v8 guides https://pixijs.com/8.x/guides
- PixiJS coding-agent skills https://pixijs.com/llms
- Community filters compatibility/imports https://github.com/pixijs/filters
- Sound v8 pairing https://github.com/pixijs/sound
- PixiJS filter API / custom `Filter.from` https://pixijs.com/8.x/guides/components/filters
- Spritesheet docs https://pixijs.com/8.x/guides/components/assets/spritesheet
- Fontsource https://fontsource.org/fonts/source-serif-4 and https://fontsource.org/fonts/source-sans-3

## 2. Proposed frontend-owned source layout (DIRECTOR BASELINE)

`FRONTEND_ROOT` is discovered, not fabricated. If the repo already has equivalent components, reuse/adapt them rather than blindly creating duplicates.

```text
<FRONTEND_ROOT>/
├── public/game-assets/                  # served **derived/runtime** files only (after verified copy)
│   ├── backgrounds/snow_arena.webp
│   ├── haunted_candle.png               # or generated optimized atlas sequence
│   ├── smoke_transition.png             # or generated optimized atlas sequence
│   ├── sounds/<nine approved originals or legal runtime encodes>
│   └── generated/<grey-wall atlas, character/FX atlas JSON and textures>
└── src/ghost-lap/
    ├── config/
    │   ├── assetRegistry.ts              # only approved paths and generated atlas mappings
    │   ├── uiTokens.css                  # typography, colours, panels, responsive
    │   ├── effectPresets.ts              # charge, spectral, night, CRT, candle presets
    │   └── audioCues.ts                  # verified filenames and semantic cue names
    ├── adapters/
    │   ├── PresentationAdapter.ts        # backend owner fills verified mapping
    │   ├── semanticViewTypes.ts          # frontend-facing read-only view DTOs
    │   └── viewStateReducer.ts           # visual state only, no physics
    ├── pixi/
    │   ├── GhostLapStage.ts              # one PixiJS Application, ticker, resize, destroy
    │   ├── SceneLayers.ts                # environment, light, ghosts, players, effects
    │   ├── WorldTransform.ts             # authoritative dimensions -> scene pixels
    │   ├── SnowArenaView.ts
    │   ├── GreyWallRenderer.ts
    │   ├── CandleView.ts
    │   ├── CandleLightmap.ts
    │   ├── NightEnvironmentGrade.ts
    │   ├── CharacterView.ts
    │   ├── FacialExpressionView.ts
    │   ├── ChargeHalo.ts
    │   ├── GroundShadow.ts
    │   ├── SpectralGhostEffect.ts
    │   ├── SoulfireShockwave.ts
    │   ├── CRTTransition.ts
    │   ├── SmokeTransition.ts
    │   └── HistoricalMontageView.ts
    ├── audio/
    │   ├── AudioDirector.ts
    │   └── GhostAmbienceSequence.ts
    ├── ui/
    │   ├── primitives/                   # FrostPanel, Button, Input, Status, Dialog...
    │   ├── screens/                      # Home, Lobby, Arena, Results, History, Spectate...
    │   ├── hud/                          # Charge, Score, Timer, Overlit, GhostDelta
    │   ├── history/                      # HourglassMark, Timeline, BranchCompare
    │   └── states/                       # Connecting, Preparing, Saving, Error, Empty
    └── tests/
        ├── expressionStates.test.*
        ├── historyTickAndFork.test.*
        ├── audioSequence.test.*
        └── presentationTransitions.test.*
```

These paths are **new proposed frontend code-module names**, not existing user Mac files. New files belong only under verified `FRONTEND_ROOT`, not inside raw `game-assets` except generated data. Adapt `src/` layout to the repo's actual conventions.

## 3. React/Pixi lifetime rules

- React owns viewport mount (`div`), HUD and menus. Pixi stage created **once** when game canvas mounts; cleaned up once on teardown; do not reinitialize Pixi `Application` for each state snapshot.
- Use an exclusive scene state (`MENU_PREVIEW`, `DAY_GAMEPLAY`, `HISTORY_VIEW`, `PREPARE`, `TRANSITION`, `NIGHT_GAMEPLAY`, `RESULTS`) derived from actual app lifecycle; modes do not change backend state automatically.
- World coordinates map consistently via a single transformation. `resize` recomputes **presentation** scale and HUD anchoring only; player/wall colliders and authoritative positions never change.
- Scene has well-separated environmental and player layers to avoid accidental filter inheritance. Apply grade to environmental root only; halo behind crisp head; ghost behind living; UI above scene; smoke as transition overlay.
- Asset preloading has a stable cache. Each `AnimatedSprite` shares frame textures. Only one `AudioDirector`; de-dup events between resync/replay/live.
- `destroy()`/unmount and rematch clean up effects/tweens/listeners/audio handles; stable Sprite instances for existing players; update properties rather than recreate whole trees every tick.
- Avoid React setState at 30–60Hz for all moving world sprites. Use the Pixi render adapter to mutate/animate sprites from verified view frames; React state only for meaningful UI changes. The app should remain responsive under 30Hz network and 60Hz render.

## 4. Semantic adapter types (illustrative; **NOT backend DTOs**)

```ts
// Design contracts only. Backend owner maps actual game types, keys and fields.
interface ScenePresentationState {
  visualMode: 'day' | 'night' | 'history' | 'transition';
  world: { width: number; height: number };   // from verified backend configuration
  players: ReadonlyArray<{
    identity: string; characterSlot: 1|2|3|4;
    x: number; y: number; charge: 0|1|2|3|4;
    movementFacing?: 'left'|'right'|'up'|'down';
  }>;
  ghost?: {
    sourceIdentity: string; x: number; y: number;
    // facial expression is ALWAYS evil for the ghost view
  };
  candlePositions: ReadonlyArray<{ id: string; x: number; y: number }>;
  wallGeometry: unknown; // backend owner replaces with actual verified model
}

type SemanticVisualCue =
  | { kind: 'CANDLE_COLLECTED'; stableEventKey: string; actor: string }
  | { kind: 'SOULFIRE_BURST'; stableEventKey: string; actor: string; x: number; y: number }
  | { kind: 'PLAYER_HIT'; stableEventKey: string; victim: string }
  | { kind: 'UNEQUAL_CONTACT'; stableEventKey: string; victim: string }
  | { kind: 'MATCH_FINALIZED'; stableEventKey: string };
```

**Warning:** `stableEventKey` is required by audio/visual dedupe, but source server event shape must supply or permit a backend-approved derivation. This example does not grant the frontend permission to invent IDs. Same for wall geometry and slot assignment.

## 5. Loading and fonts

- Load fonts before initial menu measurements; `document.fonts.ready` when appropriate. Cache all required scene textures before displaying gameplay. Don't show blank shapes then replace them with real ones mid-match.
- PNG animation sheets may exceed GPU dimensions or memory; run atlas audit and repack before use. Never assume a giant all-frames one-strip sheet is loadable on an integrated GPU.
- Render stage `resolution`/`autoDensity` should respect display density but profile first; cap pixel ratio if needed. Use sprites and environment composite rather than many per-object filter passes.

## 6. `@pixi/react` policy

Optional ONLY if existing verified frontend is React 19+ and already uses `@pixi/react`; otherwise use stable imperative Pixi with a React wrapper. No React package upgrade solely for this project. Keep DOM menus separate from WebGL canvas.
