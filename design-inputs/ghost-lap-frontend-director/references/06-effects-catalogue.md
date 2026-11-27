# 06 — Rendering effects catalogue; exact allowed techniques and exclusions

**Packages:** PixiJS v8 `pixi.js` for `ColorMatrixFilter` and custom `Filter`; `pixi-filters` **v6** for `ShockwaveFilter` and `CRTFilter`; see `09` for literal install/import commands. Effects are view-only, never gameplay conditions. All presets live in one effect-config module, not hardcoded across scene classes.

## A. Four charge appearances with five numeric states — CONFIRMED

**Use a separate coloured *shape-backed halo* behind the living head, not an expensive full-body Glow/Bloom stack.** The body and face remain original, crisp and unaffected. Match body hue: orange, pink, yellow, green. Do not use a universal yellow aura. Halo should follow the head silhouette (circle/square/diamond/rounded square), not an indiscriminate round blob covering the face.

| Charge | Expression | Behind-character halo | Notes |
|---|---|---|---|
| 0 | frown | OFF | visually same as 1 |
| 1 | frown | OFF | first candle causes no new expression/halo |
| 2 | neutral | very faint | imperceptible at glance is acceptable if face conveys state |
| 3 | happy | faintly stronger | never dominates candle |
| 4 | super happy | readable modest armed halo | distinctly stronger than 3, still tasteful |

**DIRECTOR BASELINE visual tuning seeds** (not fixed percentages): halo sprite alpha `0,0,0.08,0.14,0.28` for 0/1/2/3/4; width expansion approx 5–8% beyond silhouette; feather outward only; stage transitions 100–150ms. Numbers are visual starting points to be tuned with actual textures at game size, not requirements derived from the user's illustrative '2–5%' remark. No internal character colouring, and no GlowFilter on the base sprite. Cache halo texture once per shape; adjust `alpha`, `tint` and `scale` only.

**Interaction with release:** when a real 4/4 release occurs, briefly emphasize this existing halo while the screaming expression appears, then return to the actual new charge stage when authoritative state changes. Do not assume release duration or charge deduction; source from events.

## B. Soulfire Burst — CONFIRMED PURE shockwave + existing halo pulse

Source: `ShockwaveFilter` in `pixi-filters/shockwave`; actual contract may expose `center`, `radius`, `time`, `amplitude`, `speed`, `wavelength`, `brightness` (verify current version). Apply to appropriate **game world pixels only**, never entire browser DOM or menu. Center at actual Burst position converted through world/viewport transform, update elapsed effect time only while active, then remove or reuse an inactive filter.

**Hard exclusions:** no added yellow or other tint; no hand-drawn concentric coloured ring; no spark/powder particle burst; no RGB split; no permanent screen wobble; no camera zoom/shake; no heavy Bloom filter. Allowed visual companion: a **brief, modest increase of emitting player's existing shape-backed halo**. If filter brightness property subtly shifts intensity, leave neutral `brightness=1` by default: do not treat brightness as 'yellow tint'.

**Authoritative ownership:** radius, eligibility, hit detection, exact interval, Overlit effects and outcome are server-owned. If the true impact duration/range isn't present in a DTO, backend agent must provide an event mapping; **no made-up collision cylinder**. Distortion maximum visual radius can be tuned to correspond to known server effect, but must never misrepresent gameplay reach dramatically.

**Perf:** instantiate/reuse one filter per concurrent visible Burst where reasonable, and constrain filter area to arena region or clipped local region; do not run 60fps full-screen Shockwave when inactive. In multiple simultaneous Bursts, obey resource policy and degrade to visually gentler single shared distortion rather than dropping frames; the sound and result remain authoritative.

## C. Spectral ghost — CONFIRMED presentation / DIRECTOR BASELINE shader

**One shared `SpectralGhostEffect`, per ghost view**. Assemble `Kenney head shape + permanently mean/evil face` in natural source pixels; feed composited pixels to lightweight shader with: luminance `L = 0.2126R + 0.7152G + 0.0722B`, map dark L to blue-teal shadow, midtones to translucent cyan, bright L to near-white cyan; preserve original pixel alpha and facial contrast. This maps **all four original body hues to the same recognizable cold spectral palette**.

**Material seeds (art direction):** darkest `#1d516a`; mid `#62b9c5`; near-white highlight `#e8ffff`; body final alpha visually approx **0.55–0.72** (tune against daytime/nighttime snow and overlap); edge cyan `#83e4eb` faint. Use `Filter.from({gl:{fragment},resources:{...}})` on Pixi v8 (GLSL ES 3 syntax) with verified uniforms and version-specific APIs. One pass preferred; a reused subtle edge glow/outline texture behind ghost acceptable if edge emission itself is cleaner rendered separately. No body blur, no jitter trail, no ghost lightmap/candle-like environmental illumination. Only ghost appearance changes; movement is authentic history.

If premultiplied-alpha or shader treatment makes eyes disappear, fix colour mapping/contrast, not physics. Ghost's extreme alpha must never make it invisible on pale snow. Ghost is behind living head when coordinates overlap; cyan edge outside the silhouette still faintly shows. Never introduce a visual x-offset just to prove divergence.

**Performance fallback:** lower-resolution edge texture or disable shimmer (shimmer is OPTIONAL/DEFERRED); spectral luminance recolour and ghost identity **must remain**. No per-frame texture readback or CPU colour-mapping every tick.

## D. Nighttime snowy environment — CONFIRMED concept / DIRECTOR BASELINE implementation

**Do not call `ColorMatrixFilter.night()`** — the built-in preset simulates green night vision, not cool snowy darkness. Grade the **background and grey walls together**, not living player, spectral ghost, candle flames or React UI. Use a single `ColorMatrixFilter` / `AdjustmentFilter` / custom 1-pass colour matrix; no separate permanent vignette or film-grain pass. Starting appearance: icy blue/slate shadows, retained pale snow detail, monster shadow visible, enough base luminance for navigation; not pitch black.

Night mode only in ghost challenge/fork **night gameplay states**, never live multiplayer, never solo original. If historical replay is of daytime original, show daytime unless the recorded context explicitly asks for night presentation; do not add ghost night to every replay indiscriminately.

**Important visual separation:** after environmental grading, composite shared warm radial lightmap to snow/walls. Candles shine through, living head remains normal colour, ghost remains spectral. Snow should not turn teal-green from green `night()` preset.

## E. Candle lightmap — CONFIRMED, see `05`

Same orange animated `haunted_candle.png` across day/night. Day barely perceptible warm spot; night more diffused warm spot on environment; no flicker pattern change, no hard falloff or real-time wall shadow casting. Use shared batched/procedural low-res lightmap texture; `pixi-filters/simple-lightmap` is an option after checking its API/scene fit, not magical auto-light calculation. Never 1 full-screen filter per candle.

## F. CRT transition — CONFIRMED

`CRTFilter` from `pixi-filters/crt` on **game view during light-to-night transition**, brief restrained static/interference (DIRECTOR BASELINE target 250–500ms within scene transition, **not** seconds of persistent strobe). Do not use on each hit, login page, or whole browser. Avoid high-frequency flicker; reduced-motion variant = one 150ms opacity shift or static interference still frame.

No `CRT_glitch.wav` exists as a confirmed cue. Do not fabricate it, auto-play unknown WAVs or include it in approved manifest. A future sound may be added after user selects it.

## G. Smoke transition — CONFIRMED downloaded spritesheet

`smoke_transition.png`: imported as transparent `AnimatedSprite` or packed atlases, nominal 84 frames at 30fps (~2.8 sec) **VERIFY physical sheet**. Use only as a transition overlay. Light/day scene behind until smoke coverage masks switch; swap presentation to night under full coverage, then reveal. If smoke asset doesn't become fully opaque, use a neutral matching mask/opacity crossfade (not new character art) to prevent one-frame visual pop. Do not loop indefinitely as gameplay fog; remove from render tree after transition.

**The user described approximately 3s for the light-to-night transformation.** Treat this as a presentation target, **not backend loading success condition**. Slow preparation keeps an honest ready-wait UI after animation. No duplicate audio transition unless approved.

## H. Rewind speed montage — PROPOSED visual, backend data required

User's visual goal: time-compress real recorded history into about **1 second** of backwards playback, ending **exactly** at selected fork tick. Relative speed depends on chosen interval; not a fake reverse video shader. Given recorded frames `F[0..N]` and selected `T`, render samples in decreasing tick order, such as `i(u) = round(N + (T-N)*ease(u))`, where `u` is visual progress `0..1`. Never mutate authoritative state while previewing or silently interpolate through missing/corrupt frames. Ideally use preloaded genuine positions/events; not a DB query for every screenshot frame.

A tiny CRT edge during conclusion is permissible if already part of transition; do not over-add blur/glitch/zoom. If recording range is absent/unavailable, show truthful preparation UI rather than fake footage.

## I. Negative catalogue — **do not add**

- Character body Bloom/AdvancedBloom, persistent blur, cinematic lens flare, glowing screen tint on Burst.
- Player-hit glitch/RGBSplit, extra shockwave colour overlay, random particles, permanent fog vignette.
- Artificial ghost trail, invented ghost movement, flashbang brightness, night filter over the player or HUD.
- Extra bat transition, horror screams/voiceovers/laughs, unsupported modal mechanics.

## J. FX quality checklist

Each effect has one owner, one controlled lifecycle and a disposal path: trigger source verified, input coordinates transformed once, no duplicate event replay, deactivation clears GPU resources, visuals correct at 0/1/2/3/4 and both day/night, contrast readable at gameplay scale, reduced-motion override, no gameplay coupling. Do not pass solely from a still screenshot; test after repeated start/stop/rematch/history switches.
