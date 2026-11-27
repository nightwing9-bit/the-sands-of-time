# Spec 13.10: hourglass timeline, replay and comparison

Creative implementation done and checked against the real local stack. Human review and technical acceptance are pending. Fork and Ghost Challenge actions are Task 13.11.

## Try it

Vite on 5174 is already serving this code with hot reload. Open [My Histories](http://localhost:5174/?screen=histories), select an Original, then **Replay & choose a fork point**. A saved future (Saved Futures tab) opens through **Replay & compare with the original**. Both URLs survive refresh: `?screen=replay&replay=original|child&id=<uuid>`.

Keys on the rail: Left/Right one tick, Shift+Left/Right one second (30 ticks), Home/End, `,` and `.` previous/next recorded event. Space plays or pauses viewing (anywhere outside a button). Escape returns to My Histories.

## What changed

| Area | Files |
|---|---|
| Wire (strict parsers, bounded reads) | `src/history/historyWire.ts` |
| Immutable frame store, chunk loader, identity-keyed cache | `src/history/historyStore.ts` |
| Pure rules: time text, keys, fork verdict, playback, same-tick comparison, ribbon | `src/history/model.ts`, `src/history/target.ts` |
| Pixi scene (one head, optional original ghost) | `src/history/HistoryScene.ts` |
| Dock (hourglass left, rail, transport, Fork here) | `src/history/HistoryDock.tsx`, `src/styles/history.css` |
| Screen (HUD, load/seek/play, portal into the dock) | `src/history/ReplayScreen.tsx` |
| Wiring | `GhostLapApp.tsx` (replay screen, URL, dock host), `screens.ts`, `HistoriesScreen.tsx`, `HourglassMark.tsx` (2.5D depth, `flowing`), tokens (`--gl-ice`) |

## Behavior and rules

- **Dock is contextual.** It exists only on the replay screen, below the stage. The whole 16:9 arena re-fits above it (measured 1440x900: stage 1270x715 above a 153 px dock; 1280x720: 1016x572 above 122 px).
- **Loading.** One history is one detail read plus at most ten sequential 300-state trace requests (the tenth holds only T2700). Dragging toward an unloaded part moves that one chunk to the front. Measured on the real host: 10 trace requests to load a full Original, 0 more for a pointer click, a 60-step drag and 40 key seeks. A full cold load took about 8 s (dock usable after about 3 s).
- **Identity.** Frames are cached by `kind:id:finalStateHash:versionKey` and pinned to the library row. A slice about another id, kind, hash, rules version, source or fork tick is refused and stores nothing. A held state can never change between reads. A refused slice is not retried.
- **Child is never an Original.** `/children/{id}/trace` and `/originals/{id}/trace` have separate parsers and cannot fill each other's history. A refresh on a child URL used only `/children/{id}`, `/children/{id}/trace` and `/children/{id}/comparison`.
- **Fork rule.** Allowed only for a confirmed Original at T0..T2699. T2700 shows "Nothing is left to change at T2700". A saved future shows "can't be forked again". The button is not wired until 13.11 supplies an action, and says so in visible text.
- **Selected vs viewed.** Gold diamond = selected tick (what Fork here would use). Ice playhead = tick being viewed during local playback. Pausing selects the paused tick. Playback is local (30 ticks per real second); it never touches the server.
- **Comparison.** Through the fork tick both worlds are one state, so both scores are equal and shown as one shared, heavier line. After it: exact scores if the original's trace is loaded (opt-in "Show the original's path", which also draws the original's recorded head as the cyan ghost), else a curve point exactly at the tick, else the server's earlier sample labeled with its own tick. The curve is one request (271 points, stride 10). The ribbon under the rail is the server's own delta samples joined by straight segments.
- **Honest loading.** An unloaded tick keeps the last real state dimmed with "Loading T…". Nothing is interpolated across a missing state. Playback stops on the last loaded state and waits.

## Real-host evidence (disposable guest, headless Chromium, local Compose)

- Real Original `01a1f348-2f96-7931-b389-e135fa088bf8` (yellow diamond, saved score 1670). Replay: T0 score 0; T1140 `00:38.000` score 707; T2700 `01:30.000` score 1670 (equals the saved score), Fork disabled with its reason; T2699 Fork enabled.
- Real Fork child `01a1f34c-2673-7a0b-9e69-db10d2acf6de` at T1140 (my throwaway hub driver played it badly: 764 vs 1670, server delta -906). At T1140 original 707 = future 707, delta 0. At T2700: 1670 / 764 / -906, matching the server's saved delta. At T1500 with the original loaded: 919 / 709 / -210. At T2341 without it: labeled sample from T2340.
- Console had no errors. The dev fixture strip is development-only.

## Decision to confirm: the shared scrubber

The workspace rule says one React scrubber serves both apps, and the 13.1 study comment planned to wrap `@sands/timeline-ui`. That component is adapter-driven: each verified seek calls the server (`preview/restore`) and it owns a fork flow. This task requires local seeking over loaded immutable frames and forbids a request per movement, so the dock is Ghost Lap presentation (`HistoryDock`) and `@sands/timeline-ui` is unchanged. The bundle-graph check still passes because the greybox keeps using the shared scrubber. Options: keep as is, or later add a local-frames mode to the shared package (touches Revenant, so it needs its own task and compatibility check).

## Dependencies and defects

1. **Candle positions in saved histories (named dependency, not blocking).** `SoloGhostFrame` carries `activeCandleIds` but no coordinates, and the plan cannot be rebuilt in the browser (the Int64 seed is rounded by JS and a second plan engine is out of bounds). The replay shows the exact lit-candle count, pickups and events, and says candles are not drawn. Smallest fix: add the existing additive `VisibleCandles`-style projection (as `SoloMapping.ToOriginalFrame` already does for live Original frames) to `ChallengeMapping.ToWire(SoloTraceFrame)` using the pinned plan for that original's seed. Success check: a trace chunk carries `visibleCandles` whose ids equal `activeCandleIds`. Creative work can continue; 13.11's night reveal wants it too.
2. Cold full-history load is about 8 s on this machine (server verifies per request); the UI is usable after the first chunk.
3. Burst release faces and halo pulses are not replayed (no cue replay in history by design).
4. Not run: Safari, high-DPI, FPS or memory measurement, screen-reader pass, keyboard-only walk of the Histories screen.

## Questions for the user

1. Does the hourglass sitting left of the rail, with the sand tracking the viewed tick, read as the signature of the game at a glance?
2. After the split, do the shared line and the cyan score-gap line explain "same past, different future"? Or do you want the original's path shown by default?
3. Is "Replay & choose a fork point" the right entry, with the Fork button visibly waiting for the next build?
