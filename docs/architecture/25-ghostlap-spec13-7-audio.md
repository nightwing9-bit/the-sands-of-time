# Spec 13 task 13.7: the nine approved sounds and the AudioDirector
Status: **CREATIVE IMPLEMENTATION DONE · focused technical gate passed · HUMAN REVIEW PENDING (the whole mix) · TECHNICAL ACCEPTANCE PENDING (items below).** Existing `web/apps/ghost-lap` only. No backend, Marrow, `ghost-v1` / `ghost-mp-v2` / `arena-v1`, scoring, collision, Burst or record change. No new dependency. No sound outside the nine approved files.

## Try it
Existing dev server, same URL, hot reload: `http://localhost:5174/?screen=soundStudy` (or "Sound study" in the preview strip). Your first click anywhere unlocks sound (a browser rule); until then the Home screen shows a small "Sound is off · click to enable" pill. If the server is not running: `npm run dev -w @sands/ghost-lap` from `web/`.
The study is a labelled **PREVIEW FIXTURE**: its buttons stand in for the screen that would claim a mode or the confirmed server event that would own a sound. Everything audible goes through the real `AudioDirector`.
1. **Mood**: Menu loops `gameLoading.wav`; Day loops `multiplayer_theme.wav`; Transition is quiet by design. Press **Night** from Menu or Day: it is refused (readout says so, nothing plays). Go Transition, then Night, or use **Day → Night**: `ghost_main_theme.wav` and the ambience start together.
2. **wind, wind, knock**: the three chips show the running segment (about 9 s, 9 s, 3.9 s, then again). **Skip to next segment (dev)** reaches the knock quickly.
3. **Effects**: Candle, Weaker contact, Burst each play once. **Same event again** re-sends the last key and must stay silent. **Ghost candle** must stay silent. **UI click** plays `menu.wav`.
4. **Volume**: Music and Effects sliders and Mute (also at `?screen=settings`, and Home → Settings).
5. **Per-sound trims** (fold-out): move a slider while a sound plays, report the dB numbers you like. Session only.
Real play: `?screen=dayPlay` plays `multiplayer_theme.wav` only while a run is actually being played, and candle/contact/Burst sounds come from real events (details below).

## What was built
| Piece | File | Notes |
|---|---|---|
| Cue registry | `src/audio/cues.ts`, `src/config/assetRegistry.ts` | exactly nine entries, served from `/game-assets/sounds/` (unchanged byte copies; masters stay in `design-inputs/game-assets/sounds/`) |
| State machine | `src/audio/AudioDirector.ts` | pure rules, tested with a fake engine. Mode claims, music, night ambience, SFX de-duplication, volume/mute, dispose |
| Engine | `src/audio/webAudioBackend.ts` | plain Web Audio; `source -> trim -> music|sfx bus -> master`. Installed packages checked: `@pixi/sound` is not installed and is not needed |
| Browser wiring | `src/audio/gestures.ts`, `instance.ts`, `react.ts` | unlock on first trusted pointerdown/keydown/click; `menu.wav` on real UI activation; one app-lifetime instance, disposed on hot reload |
| Play bridge | `src/audio/playAudio.ts`, `PlaySession.onEvent` | maps authoritative events to cues; `onEvent` is a read-only tap added to `PlaySession` (one call per event key, listener errors cannot disturb play) |
| UI | `ui/AudioControls.tsx`, `ui/SoundPill.tsx`, `screens/SettingsScreen.tsx`, `screens/SoundStudy.tsx` | Settings is real (sound only); the study is dev-only |

## Behaviour decisions (all in code, all reversible)
- **Modes by claim.** A screen claims a mode while mounted; the latest claim wins; releasing returns to the one below (menu if none). `PlayScreen` claims `day` only while a run is truly playing (has identity, not final, not failed/ended); the start panel, lobby, a finished run and a failed one stay on `gameLoading.wav`.
- **Night gate.** `night` is accepted only directly after `transition`. A claim for night from menu/day stays in quiet `transition` and reports `nightBlocked`, until the claim leaves night. This is the structural form of "never during preparation or failed transition". **No real night gameplay screen exists yet** (Challenge/Fork arrive in 13.10/13.11), so those screens must claim `transition` during prepare/smoke and `night` only after the verified ready gate. The study shows the contract.
- **Ambience.** One source at a time, sequenced only by the engine's natural-end callback (`ended`). A generation number kills every pending callback when night ends, so a late `ended` cannot start anything. Verified in a real browser: the observer saw step changes at 7.5 s, 16.5 s, 20.3 s and 29.4 s after it began watching (the first wind had already been playing for part of that time). The later gaps are 9.0 s, 3.8 s and 9.1 s, which match the measured wind and knock lengths.
- **Mute is gain zero**, so the ambience keeps its place while muted and no timers need cancelling. The skill text says to cancel callbacks on mute; I kept the sequence running silently instead (no restart from step 0 on unmute, nothing audible, nothing leaks). Say if you prefer the literal behaviour.
- **Which events sound.** Candle: only the local player's own pickup. Hit (`player_hit.wav`): only when the local player is the weaker victim of `ContactDrain` (`event.slot` is the victim, as the existing feed and fear face read it). Burst: any emitter's confirmed `BurstManual`/`BurstAuto`. `BurstHit`, key presses, spectators' pickups and every ghost/historical cue are silent. Questions 2 and 3 below ask whether local-only is right.
- **De-duplication.** Key is `kind:` + `matchIdentity|server event key`; bounded memory (512); a new identity (rematch) resets it. Events delivered more than 30 ticks (1 s) after they happened are treated as backlog and stay silent (a presentation choice, not a game rule).
- **Locked or muted never breaks play.** Before unlock nothing is queued or started; SFX are dropped. Overlapping SFX are capped at 8.
- **Hidden tab.** Nothing is paused and nothing in game state listens to visibility. On return the audio context is resumed (Safari can suspend it).
- **Settings** (music/effects volume 0 to 100, squared taper, mute) persist in `localStorage` key `ghost-lap.audio.v1`. Night ambience follows Music; there is no third slider.

## Loudness: what was measured and what is only a first guess
Measured on the actual files with ffmpeg `ebur128` and `volumedetect` (19 Nov 2026):

| File | Integrated | Peak | First-pass trim |
|---|---:|---:|---:|
| `ghost_main_theme.wav` | -14.4 LUFS | -1.0 dB | 0 dB (reference = 100) |
| `gameLoading.wav` | -12.3 LUFS | 0.0 dB | -2.1 dB |
| `multiplayer_theme.wav` | -12.0 LUFS | -4.1 dB | -2.4 dB |
| `Gushy_wind.mp3` | -10.1 LUFS | 0.0 dB | -6.3 dB (about 2 dB under the theme after normalising, for "80") |
| `Ghost_knocking.wav` | -17.4 LUFS | -0.5 dB | +2.0 dB (about 1 dB under the theme, for "90") |
| `blast.wav` | -6.2 LUFS | -0.2 dB | -6 dB (loudest file) |
| `player_hit.wav` | -17.8 LUFS | -1.0 dB | +1 dB |
| `candle_eat.wav`, `menu.wav` | too short to gate (mean -20.1 dB) | -0.2 dB | 0 dB, -4 dB |

Important finding: the wind file is **hotter than the night theme**, so a literal "80% gain" would put it above the music. Trims normalise to the night theme first, then apply the skill's ratios. Bus headroom: music bus -4 dB, SFX bus -3 dB, master -2 dB, no compressor. The conversion from "80 / 90 perceptual" to dB is an estimate. **Every number is unlistened and HUMAN REVIEW PENDING**; the study's trim sliders exist so you report values instead of the code guessing them. Edit `trimDb` in `src/audio/cues.ts` afterwards.
Fades are director baselines: 400 ms music in and out, 300 ms ambience out, 12 ms de-click. Not listened.

## Checks actually run
- `node --test test/audio.test.ts`: **13 pass** (nine files exist, served from `/game-assets/sounds/`, byte-identical to the masters; locked state; mode routing and one BGM; night gate; wind-wind-knock by completion; stale callbacks after leaving night; claims; once-per-key SFX incl. ghost origin; mute, cap and click throttle; settings persistence and corrupt storage; dispose; event mapping; real `PlaySession` end-to-end dedupe, backlog, rematch). Whole app suite: **51 pass**. `tsc`, `vite build` and `check:bundles` pass.
- **A bug found only in a real browser** (the fake engine hid it) and fixed: starting a source made the engine announce a load change before `play()` returned, which re-entered `reconcile()` and recursed until the stack overflowed (no sound at all). `reconcile()` now refuses re-entry, and the fake engine now announces during `play()` so a test covers it.
- Headless Chromium (software GL) against the dev server, real trusted clicks: locked on load, unlock on first click, menu loop, day theme, night refused from day, transition silent, night theme + wind, ambience sequence 0 → 1 → 2 → 0 → 1 over 34 s, candle/hit/Burst sounded once, repeat and ghost silent, leaving night leaves `gameLoading.wav` only and no ambience, no page errors; Settings slider/mute persisted; `menu.wav` plays for real controls and not for the dev panels. **Headless has no speakers: this proves routing and timing, not that anything sounds good.**

## Pending / dependencies (exact)
1. **HUMAN REVIEW PENDING:** day/menu/night balance, wind and knock level against the theme, Burst vs music, whether `menu.wav` and `candle_eat.wav` are too similar (their measured stats are identical), fade lengths.
2. **TECHNICAL ACCEPTANCE PENDING:** Safari/Firefox playback (only Chromium headless ran); a real multiplayer match through `MultiplayerTransport` and the real Solo transport sounding candle/hit/Burst (the code path is the same `PlaySession.onEvent` but a live run was not available: test host and realtime were not running here); a hidden-tab and long-run (leak) listen.
3. **Night gameplay does not exist yet.** The director, gate and ambience work (study + tests), but no screen plays real night until 13.10/13.11, which must claim `transition` then `night` after the ready gate.
4. **File size.** `gameLoading.wav` (14.8 MB) and `ghost_main_theme.wav` (13.4 MB) are 24-bit PCM; the whole set is about 36 MB, fully fetched and decoded after the first gesture (sequentially, menu first). A smaller derived runtime format (e.g. OGG/AAC after verifying Chrome/Safari decode) is a later, separate step that must keep the masters and the cue names. Not done, not claimed.
5. **Rights.** Redistribution evidence is still missing for all nine (audit B8: only embedded artist tags for three files, no licence). The copies in `public/game-assets/sounds/` are for development; this blocks public release, not this task. No grant is claimed.
6. **No backend need found.** Server event keys already exist (`MatchEvent.Key`; Solo `frame:tick:index`). If a future Solo sender omits `Events`, candle/Burst sounds will be silent there; the HUD already says "Event history is incomplete" for that case.
7. Not included (by design): sounds for saving, CRT, seek or winner; any sound outside the nine; a third ambience slider.

## Questions worth judging
1. Does the night mix hold together: is the wind present but under the theme, is the knock noticeable without startling you, and does a Burst or candle cut through while the wind plays?
2. Day to night: is the quiet gap during the transition right, or should something already be audible when the smoke clears?
3. Should you hear other players' candle pickups and hits in multiplayer, or is local-only right?
