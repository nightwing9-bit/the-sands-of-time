# 11 — Acceptance gates, QA, risk register and intentionally open facts

This is the frontend QA contract, **not a backend test plan**. Backend agent owns authoritative event wiring and its own tests. A trustworthy skill records what it cannot know and gives the implementing agent a deterministic way to obtain it.

## A. Definition of done: scene/visual quality

- [ ] **Snow background only.** `snow_arena.webp` displayed undistorted, lossless-looking, monster shadow intact; no grass imagery or AI rough gameplay mockup shipped.
- [ ] **Wall geometry.** Actual backend-authorized maze rendered with grey Kenney tiles; no pixel/collider mismatch, no brown left, endcaps seam clean, walls don't visually suggest walkable spaces that are blocked.
- [ ] **Four living heads.** Orange circle, pink square, yellow diamond, green rounded square; no limbs, readable facial components; direction changes pupils, not face/head rotation.
- [ ] **0/1 appearance identical.** Frown and no halo; 2 neutral/faint, 3 smile/near-faint, 4 biggest happy/armed yet not brighter than candle. Numeric HUD still truthful 0–4.
- [ ] **Authoritative reactions.** 2s X-eyes only actual Blast victim, 2s fear only weaker collision victim, 4/4 release scream, genuine winner expression, consistent restoration after override.
- [ ] **Ghost.** Exact past positions, evil face always, cyan-whitish material, readable facial detail, translucent not invisible, slight edge emission behind living, no fake offset/no physical interaction/body blur.
- [ ] **Day/night separation.** Day multiplayer+solo bright; night only approved ghost play; environment darkens cool, living head remains full colour, candles warm/soft, HUD unaffected, no vignette/green night vision.
- [ ] **Burst.** Distortion shockwave + existing halo pulse; **no tinted ring**, random particles or hit RGB glitch. Repeated/multiple Burst does not leave filters installed forever.
- [ ] **CRT/smoke.** brief CRT and validated smoke transparency/coverage, no sudden visual frame pop, respects reduced motion, removes itself after use.

## B. Definition of done: UI aesthetic/interaction

- [ ] Main menu controls exist exactly within supported scope; original atmosphere, airy composition, Source Serif 4 headings/Source Sans 3 readable controls, consistent frosted panel treatment, no copied Alto artwork.
- [ ] Lobby create/join/readiness/spectate and character identity correct; no fake selector if API absent.
- [ ] Multiplayer HUD uses real score/time/charge/Overlit and sparse edge overlays, does not crop arena or obstruct action. No global leaderboard.
- [ ] Real match results show ties/forfeit/pending/finalized/aborted truth, correct match-local ranking and authorized rematch.
- [ ] Solo original includes real confirmed Soulfire/Burst when produced; save pending vs verified correctly distinguished.
- [ ] Histories split originals/futures with correct source/tick, no nested child fork control, clean empty/error states.
- [ ] Signature hourglass **left** of bottom timeline, visible only in rewind/history contexts, visually reminiscent of shared Sands of Time concept but distinctly game-like.
- [ ] Timeline includes T0–T2700 exact inspection, T2700 ineligible Fork, authentic frames/event markers/score at same tick, cursor accessible by keyboard.
- [ ] Child comparison is original vs same-tick actual child score, fork-boundary meaning correct; no banking dashboard clone.
- [ ] Settings contain Music/SFX controls and Reduced Motion; How to Play uses verified controls; no invented live pause. Keyboard focus, Escape/back and reasonable empty/wait/error flows work.

## C. Definition of done: audio

- [ ] Exact file spelling and case (`candle_eat.wav`, `gameLoading.wav`, `Gushy_wind.mp3`, `Ghost_knocking.wav`) verified against local runtime and URL.
- [ ] `gameLoading.wav` loops outside active gameplay without starting again every menu route.
- [ ] `multiplayer_theme.wav` plays in day multiplayer **and** day solo original; ghost theme starts **only after** night transition ready/reveal.
- [ ] Night audio: theme continuously, two complete wind tracks then one complete knocking track in repeat; no wind/knocking overlap and no extra voices.
- [ ] `menu.wav` on real click, `candle_eat.wav` on real pickup, `player_hit.wav` only eligible weaker-player contact, `blast.wav` on confirmed Burst; duplicate/resync suppressed.
- [ ] Stop/cancel audio on leave, game end, error, rematch transitions, mute, destroyed scene; focus and autoplay policy respected; no clipping under combined night mix.

## D. Performance target and fallback policy (DIRECTOR BASELINE)

**Target** smooth approximately 60fps on supported desktop browsers and ordinary current desktop hardware at the shipped 16:9 view; target render+UI frame budget 16.7ms (60Hz) while no uncontrolled main-thread spikes. This is a design target **not a guarantee on all hardware**. Backend latency/perf remains main agent's scope.

- On game start, preload required scene textures; allow progress/error UI for assets. A single large snow sprite is inexpensive, but a huge 90-frame candle sheet or 84-frame fog atlas may exceed GPU `MAX_TEXTURE_SIZE` or memory. Use verified atlas repacking/lowered export display resolution.
- Use one Pixi application, stable sprites, shared textures, one environment grade, **one accumulated lightmap**, one cached halo per silhouette, one spectral shader for ghost, short-lived distortion. No per-tick `new Filter`, `new RenderTexture`, canvas readback or repeated React full scene rerender.
- If visual frame time spikes: first reduce smoke atlas pixel dimensions/frame count, candle frame sizes, runtime filter render resolution; disable optional glow shimmer/extra panel blur; shorten or locally clip shockwave. **Never drop gameplay truth, precision on timeline or readable UI to gain FPS**.
- UI backdrop blur only on focused panels rather than whole screen; `prefers-reduced-motion` avoids rapid flashing; arena stage resize uses aspect-preserving world transform; no leaking resources on route changes.
- Capture browser profiler evidence with all four living players, multiple candles, one or more Bursts, night ghost and timeline interaction. Test at least Chrome and Safari on desktop, at realistic browser viewport sizes and both regular/high-DPI displays if available.

## E. Source-backed open items the skill must not invent

| Item | Status | Owner/checkpoint | Default action while unresolved |
|---|---|---|---|
| Frontend repo/project root, bundler, package manager, current React version | VERIFY | main frontend/backend agent | inspect project, do not create duplicate app |
| Mac `game-assets/` accessible to coding agent machine | VERIFY | environment/user | audit; cannot link runtime `file://` |
| True world size, wall layouts, maze dynamic/fixed and collision shapes | VERIFY | backend agent | provide `WallRenderer` boundary, no fabricated map |
| Character assignment vs actual user selection | VERIFY | backend agent | show assigned avatar, no fake selection clicks |
| Real movement and Burst key bindings | VERIFY | backend agent | How to Play labels only after confirmed |
| Initial Soulfire charge and actual solo Burst handling | VERIFY | backend game rules | **render server values**; 0/1 visuals identical; no frontend rule |
| Timeline frames/seek cache, event markers and score samples | VERIFY | backend agent | show honest loading/empty, no fake events |
| Tick/time data transport, valid fork authorization | VERIFY | backend agent | do not activate Fork until eligible and ready |
| Ready/save lifecycle after branch/history reconstruction | VERIFY | backend agent | prepared/failed/pending UI, no fake success |
| Winner/tie meaning and result payload/forfeit policy | VERIFY | backend agent | only animate actual declared winner(s) |
| Real formats/slicing for two ParticleFX PNG sheets | VERIFY | asset inspection | audit dimensions/frame count/alpha, repack |
| Actual Kenney asset PNG names/alpha/atlas coordinates | VERIFY | asset inspection | read source pack, never crop marketplace screenshot |
| Source audio creators/licences and public raw-file redistribution rights | VERIFY | owner/licence audit | local prototype okay; prevent unsupported public release |
| Exact night grade, ghost alpha, candle radius and halo alphas | DIRECTOR BASELINE for tuning | playtest/art | use central defaults in refs; adjust only visually |
| Spectator viewing of finalized results after room auth changes | VERIFY | backend agent | show when actually authorized |
| Browser fullscreen and audio focus behaviour | DIRECTOR BASELINE/VERIFY | frontend implementation | optional; don't block core game |

## F. Intentional future refinements (NOT missing baseline)

Additional screams/laughter/whispers/voiceovers, menu bespoke SFX, sound of CRT transition, art for alternate backgrounds, dynamic monsters, floating bat transition, configurable ghost shimmering, extra HUD polish. None should be slipped into first build without user approval. The user intends to iterate after playing — tune component CSS, timing, atmosphere and audio mix by experience; keep those variables isolated.

## G. Verification deliverables

When submitting frontend work, include: rendered screenshots/short capture for all major screens and modes, exact asset URLs and source licences, actual build/test commands/output, runtime performance notes (device/browser), no-errors console capture, checklist items passed/failed, unresolved VERIFY table and recorded deviations from design tokens. A finished-looking screenshot with no working interactions is **not** implementation completion.
