# 07 — Exact sound and music choreography

**All files are source assets under** `/Users/rroy/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/sounds/` (see `01`). The screenshot has shown the sounds folder, but **the full folder wasn't uploaded**; runtime must inspect names, metadata and licences. Only the nine explicitly approved filenames below are allowed in baseline game playback. Do **not** assign other WAVs yet.

## 1. Approved audio registry — literal filenames, case sensitive

| Source filename | Role | Status |
|---|---|---|
| `gameLoading.wav` | main opening/menu/lobby/history/settings music, resumes after gameplay | CONFIRMED |
| `multiplayer_theme.wav` | looping daytime BGM for multiplayer **and solo original** | CONFIRMED |
| `ghost_main_theme.wav` | looping BGM for night ghost challenge/fork gameplay **after transition** | CONFIRMED |
| `candle_eat.wav` | any confirmed candle collection | CONFIRMED; user corrected spelling |
| `player_hit.wav` | qualifying lower-charge collision with stronger participant in multiplayer | CONFIRMED; do not use on every bump |
| `blast.wav` | Soulfire Burst sound on real Burst | CONFIRMED |
| `menu.wav` | actual menu click/navigation/setting interaction | CONFIRMED |
| `Gushy_wind.mp3` | approx 9-second soft wind segment, night ambience only | CONFIRMED; this is **MP3** |
| `Ghost_knocking.wav` | knocking night ambience in scheduled sequence | CONFIRMED; this is **WAV** |

**Other sound files in folder:** candidates not approved. **Voiceovers, screams, laughs, whispers, CRT glitch sound:** DEFERRED, do NOT invent, source or play them in v1.

## 2. Audio state machine

```text
Startup (after browser gesture allows sound) ─┐
Home / screens / multiplayer lobby ─────────────┼──> gameLoading.wav LOOP (ONE instance)
My Histories / replay-viewing menu ─────────────┘         [unless explicitly entering active playback]

Active multiplayer ───────────────────────────────> multiplayer_theme.wav LOOP
Active solo original ─────────────────────────────> multiplayer_theme.wav LOOP

Preparing fork/challenge, rewind, CRT, smoke ────> TRANSITION_AUDIO_STATE
  • Keep sound presentation controlled, no night theme yet.
  • Exact choice whether menu/day music fades at start is DIRECTOR BASELINE; do not overlap 3 BGMs.

Successful ghost/night gameplay START ───────────> ghost_main_theme.wav LOOP
                                                 + AMBIENCE_SEQUENCE
Leaving/finishing/night error ───────────────────> stop night BGM and ambient sequence
Back in menu/history UI ─────────────────────────> gameLoading.wav LOOP/resume
```

**Critical:** The ghost main theme and ambient sequence start **together after the night/ghost transition ends and backend is ready**. Do not start them when just selecting an original, during failed prepare, while viewing day history, or during rewind/mask. Game-loading music persists across normal menu/character/lobby navigation without unnecessary restarts.

### Transition gaps and crossfades (DIRECTOR BASELINE)

Audio crossfade 250–500ms where it doesn't delay user actions. Soundtrack gain dips rather than overlaps long themes. When entering a ghost transition, fade existing music off around CRT/smoke; if waiting for backend readiness, optional quiet silent/frost ambience without unapproved audio asset; no artificial extra theme. Once night arena actually visible and ready, start ghost BGM + wind immediately. On exit, cancel both layers cleanly; resume menu BGM. Exact fades configurable, NOT new source decisions.

## 3. Exact nighttime atmospheric sequence — CONFIRMED

Under one continuously looping `ghost_main_theme.wav`, start this sequential cycle:

```text
          THEME:  ─────────────────────────────────── LOOP FOREVER ───────────
          AMBIENCE: [Gushy_wind.mp3]
                    [Gushy_wind.mp3]
                    [Ghost_knocking.wav]
                    [Gushy_wind.mp3]
                    [Gushy_wind.mp3]
                    [Ghost_knocking.wav] ... repeat until night gameplay ends
```

There must **never** be independent, simultaneously auto-looping wind and knocking tracks. Each begins only when the prior segment finishes. **Use audio completion callbacks**, not a hardcoded 9-second clock. Prevent double scheduling when the component re-renders or scene state flaps. Pause/cancel all queued next-step callbacks when the mode ends, focus handling requires stop, or audio is muted.

**Relative loudness direction:** night BGM = 100 reference; wind = ~80 relative; knock = ~90 relative. These are **perceptual design ratios**, **not fixed gain multipliers** or promises of equal dB loudness. Have the agent audition at combined loudness and set safe normalized gains so knocking is not startling, and wind doesn't mask pickups/Burst. Expose central `musicVolume`, `sfxVolume`; night ambience follows music mute/volume but may have its own internal relative trims. Do not add user-facing third ambience slider unless later requested.

## 4. Event SFX routing

| Real event | Cue | Suppression / duplication rule |
|---|---|---|
| backend-confirmed candle collected by relevant living player | `candle_eat.wav` | play once per unique collection; ghost's historical candle visual never triggers living pickup sound |
| backend-confirmed qualifying weaker-vs-stronger multiplayer contact | `player_hit.wav` | weaker victim only in semantic mapping; avoid all ordinary collisions and historical replays unless replay SFX enabled intentionally |
| backend-confirmed Soulfire Burst (manual or automatic if actual rule) | `blast.wav` | one per real Burst; never on button down without accepted event |
| real UI click/selection | `menu.wav` | no click sound on passive hover/render/update; avoid rapid repeated KeyDown loops |

Optional later: sound for saving, CRT, timeline seek, winner. None authorized now; no 'temporary' random Kenney sound sneaked in.

## 5. Audio manager and browser constraints

- Single `AudioDirector` initialized once at app lifetime, not recreated per screen. Prefer `@pixi/sound` v6 with PixiJS v8; `sound.add` / `sound.play` API verified with installed version. Own BGM handles and all ambient callback subscriptions.
- Browser autoplay: preload/decode while loading, then unlock/start after first valid user gesture. Audio not playing before gesture isn't a failure; surface a subtle 'Enable Sound' action if necessary.
- Handle `visibilitychange`/browser focus carefully: either pause/resume local playback consistently or according to product preference; **never pause server gameplay solely because tab hidden**. No music accumulation on tab restore.
- Read `.wav` sample sizes and loudness; if WAVs are huge, create derived browser audio (e.g. OGG/MP3 where legally allowed) **without changing source names in manifest**, and verify Safari/Chrome codecs. Short effects can stay WAV. The confirmed wind remains `Gushy_wind.mp3` source.
- Implement global mute/volume settings, prevent audible clipping and normalize perceived loudness. Avoid putting all buses at gain 1 and stacking them linearly; wind+knock+music mix must be pleasant.
- Give all active audio sources handles; stop and dispose old handles on mode exit, rematch and unmount; never leave ghost ambience in menus.
- Event de-duplication uses actual server event identity when provided; if absent ask backend agent for safe stable deduplication keys, do not invent scoring-based IDs.

## 6. Audio QA

Test: app start -> home -> settings -> multiplayer -> match end -> home; Solo Original -> menu -> Ghost Challenge prep -> night -> child save -> home; Fork abort/failure; rematch; page hidden/visible; mute/unmute halfway through knocking; rapid repeated navigation; SFX-only/music-only toggles. Confirm exact 2-winds/1-knock sequence, no overlap of daytime and nighttime themes, no double pickup on resync, no music left running after errors and no audible clipping when Burst fires during wind.
