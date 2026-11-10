---
name: ghostlap-frontend-design
description: Authoritative UI/UX and game art direction for Ghostlap.
---

# GHOSTLAP — Frontend Design & Game Art Skill

> Example scores (e.g. 1,420 → 1,578, +158) are *illustrative presentation placeholders*. UI must render real recorded values. The historical ghost is the player's own recorded solo run: read-only, non-interactive, never an AI opponent.

---

## 1. Design North Star & Signature Demo

**Creative thesis — "The Haunt Remembers."** A candlelit stone colosseum that keeps a memory of you. The living are warm (flame, gold, bone); the past is cool (cyan, translucent). Rewriting your mistake means watching two versions of yourself split apart on the same floor.

**First three seconds:** Dark slate arena seen whole. Candles flicker, rune rings glow faintly on the floor, four mascots idle in the corners. A Bungee title ignites like a wick. Two dominant actions: **Play Multiplayer** (warm) and **Race Your Ghost** (cyan).

**Mental model (identical to Revenant):** *Choose original history → scrub backward → select exact point → branch differently → watch divergence → compare.* Original runs are immutable; a rewrite inherits state, it never edits the past.

**Defining demo moment:** at ≈00:38 a candle cluster was missed. The player scrubs back, the arena reassembles, presses **Rewrite From Here**, and a cyan copy of the player *peels out of their body* and carries on along the old route while the living player takes a new one. A live ± delta tracks the gap. Finale: a huge signed delta and a saved alternate attempt.

**Principles**
1. Cyan = memory; warm light = living present. Never mix their roles.
2. Players > candles > scores > floor > decoration.
3. Every effect is a visible consequence of a real game event.
4. 2D with depth cues: soft shadows, wall faces, rim light, no perspective camera.
5. Screenshot-legible at 3 seconds, playable at 44 logical px.

---

## 2. Art Direction & Visual Identity

**Internal name: "Candlelit Colosseum."** Chunky hand-inked 2D, carved stone, living flame.

| Aspect | Decision |
|---|---|
| Geometry | Chamfered corners (12px chamfer, never round) on all UI plaques to echo cut stone. Characters are bold, rounded-silhouette shapes (heads only) with thick **2.5px dark-indigo outlines** (`#0B0C16`) and one flat shadow shape. |
| Composition | Whole 16:9 arena visible; perimeter wall band hosts the HUD so the play-space stays clear. Central *Vigil Circle* dais is the contested heart. |
| Texture | Hand-painted slate tiles, 4 variants, subtle cracks and engraved runes; walls have a lit top edge and darker face for height. Textures are stylised, not photoreal. |
| Imagery | Sparse carved skulls (4 total), 8 torch brackets, one runic ring. Pumpkin appears only as architectural finial, never clip art. |
| Light | One warm key (candles) vs. one cool accent (ghost). Floor vignette darkens edges 22%. |
| Emotion | Playful menace: wide eyes, mischievous grins; full Soulfire turns cute into dangerous. |

**Anti-patterns:** neon/cyberpunk glow, pixel art, pseudo-3D, flat blob characters with dot eyes, cartoon pumpkin clip art, cluttered HUD, purple-and-black default "spooky" gradient wallpaper, glassmorphism.

---

## 3. Complete Design Tokens

### 3.1 Typography
| Family | Role |
|---|---|
| **Bungee** (regular 400; display only, no synthetic bold) | Game wordmark/title, major screen headings, countdown, victory announcements and the signature oversized result delta. Its blocky retro-arcade character provides the game's expressive identity. |
| **Outfit** (500, 600, 700, 800; tabular numerals for changing data) | All functional UI: HUD timers/scores, buttons, player and winner names, room codes, history scores, labels, body text and timeline readouts. Readable and consistent during motion. |

| Role | Font | Size/Line | Weight | Tracking |
|---|---|---|---|---|
| Game title | Bungee | 128/112 | 400 | −0.02em |
| Screen heading | Bungee | 48/52 | 400 | −0.01em |
| Countdown 3-2-1 | Bungee | 220/200 | 400 | 0 |
| Result delta (hero moment) | Bungee | 160/150 | 400 | −0.02em |
| HUD timer | Outfit | 40/40 | 800 | tnum |
| HUD score | Outfit | 36/36 | 700 | tnum |
| Label caps | Outfit | 12/14 | 700 | +0.12em UPPERCASE |
| Body | Outfit | 16/24 | 500 | 0 |
| Button | Outfit | 16/16 | 700 | +0.04em UPPERCASE |
| Timeline time | Outfit | 18/18 | 700 | tnum, m:ss.t |

**Typography consistency:** Bungee is reserved for expressive display moments; Outfit owns all interactive UI, player names, timer/score data and supporting copy. Do not introduce a third font or vary the font system between screens. Use tabular numerals for changing values and retain the exact existing layout hierarchy; fit long display text within its allocated plaque without clipping. The large signed result **delta** is the deliberate Bungee exception to Outfit's numeric-data role.

### 3.2 Colour (dark only)
| Token | Hex | Use |
|---|---|---|
| `--void` | `#06070D` | Page/letterbox |
| `--arena` | `#12151F` | Base floor |
| `--tile-a` / `--tile-b` | `#1B2030` / `#171B28` | Floor variants |
| `--grout` | `#0D0F18` | Joints |
| `--wall-top` / `--wall-face` / `--wall-rim` | `#3A3F55` / `#232738` / `#5B6285` | Walls |
| `--surface` / `--elevated` | `#14172A` / `#1E2238` | Menus / modals |
| `--border` | `#343A5A` | Plaque rims |
| `--text` / `--text-2` / `--text-3` | `#F3EFE6` / `#B8B6C8` / `#8A88A0` | Text (≥7:1 / 7:1 / 4.6:1 on `--surface`) |
| `--flame` / `--flame-core` / `--ember` | `#FFB347` / `#FFE9A8` / `#FF6A1F` | Candles, living warmth |
| `--soulfire` | `#FFD24A` | Charge, Overlit |
| `--ghost` / `--ghost-deep` | `#7FE8FF` / `#2FB6D6` | **Original history/ghost** |
| `--alternate` | `#FFC857` | **Living/rewritten attempt** (warm gold) |
| `--positive` | `#7DF0A9` | Ahead / improvement |
| `--negative` | `#FF6B7A` | Behind / danger |
| `--warning` | `#FFB347` | Connection warning |
| `--disabled` | `#4A4C60` | Disabled |
| `--focus` | `#7FE8FF` 3px ring + 2px void offset | Focus |

### 3.3 Mascot palettes (also identity)
| Mascot | Primary | Shade | Accent | Outline |
|---|---|---|---|---|
| Morro | `#EDE6D3` | `#B8AE92` | `#FF6A1F` | `#0B0C16` |
| Wisp | `#6CF2B2` | `#0E8B66` | `#E8FFF4` | `#06251C` |
| Hexwick | `#8E4DFF` | `#4B1FA8` | `#B6E84A` (stem) | `#1A0B3D` |
| Vesper | `#E5384F` | `#8A1226` | `#FFD0D6` | `#2B050C` |

### 3.4 Spacing, radii, shadow, layers
- Spacing (4px base): 4, 8, 12, 16, 24, 32, 48, 64. Menu panel padding 32.
- Corners: **chamfer 12** for plaques/buttons; 6 for small chips; no circles except gameplay discs.
- Borders: 2px `--border` plus 1px inner highlight `rgba(255,255,255,.06)` top edge.
- Shadow: `0 12px 32px rgba(0,0,0,.55)`; glow only for gameplay states (flame, Soulfire, ghost).
- Arena grid: 1600×900 logical, 50px tile (32×18). Character visual 44–48; collision radius 22. Candle visual 28; pickup ring 36.
- Layers (bottom→top): floor 0 · floor decals 1 · candle telegraphs 2 · candles 3 · ghost trail 4 · ghost 5 · living trail 6 · player shadows 7 · players 8 · VFX 9 · HUD (React) 20 · timeline dock 30 · overlays 40 · toasts 50.
- Density: HUD ≤ 15% of arena area; min touch target 48px.

### 3.5 Effect colour rules
Warm gold/orange = living events (candle, Soulfire, Burst). Cyan = history only. White flash = damage/hit only. Red-orange = time pressure only.

---

## 4. Layout Architecture & Responsive Geometry

**World scaling:** `scale = min(vw / 1600, vh / 900)`; world centred; letterbox filled `--void` with a 24px carved-stone frame texture on the open sides. Never stretched or cropped.

**Playable area:** x 50–1550, y 50–850. **Perimeter wall band (50px)** hosts the HUD.

| HUD element | Position (logical px) |
|---|---|
| Score plaque | Top-left, x 70, y 6, 260×40 |
| Timer | Top-centre, 160×44; Outfit 40 |
| Opponent chips | Top-right, up to 3 × 150×36 |
| Soulfire sockets (4 diamonds) + Burst-ready label | Bottom-centre, y 854 (inside wall band) |
| Connection pip, pause, help | Bottom-right, 3 × 32px |
| Player identity chip | Bottom-left (face + name) |

**Replay/Challenge/Compare layouts:** world scales into `viewport_height − 96px`; the **Timeline Dock (96px)** sits below the world, full width of the world rect. At 1920×1080 → world 1750×984 (letterboxed 85px each side).

**Mobile landscape:** world fills; floating **left thumbstick** (appears at first touch in left 40% of screen, 140px base, 64px knob); **right Burst button** 96px in bottom-right safe area, 55% idle opacity, 100% when ready. **No candle slot is ever within 200 logical px of a corner**, so controls never cover pickups or spawns. HUD: timer + score only top; opponents become face+score chips. Dock 72px. **Portrait:** gameplay blocked with rotate prompt (animated phone outline); menus remain usable single-column.

**Menus:** centred plaques max 960px wide, 16:9 stone backdrop, scrollable on small screens.

**Arena layout (authored, coordinates x,y,w,h):**
| Piece | Placement |
|---|---|
| Perimeter | 50px band |
| Short walls | (350,200,300,50) · (1250,650,300,50) · (1250,150,50,250) · (300,500,50,250) · (750,200,250,50) · (600,650,250,50) |
| Vigil Circle (walkable octagon dais, engraved) | centre (800,450), radius 150 |
| Spawns | P1 (200,150) · P2 (1400,150) · P3 (200,750) · P4 (1400,750), each with engraved ring + mascot crest |
| Candle slots | 16 authored positions, 2 at the Vigil Circle edge, ≥200px from corners |
Routes loop around walls; every wall has gaps ≥100px on both ends (no dead ends).

---

## 5. Complete Screen-by-Screen Blueprints

### 5.1 Start / Menu
Entry: first load. Full arena backdrop dimmed 70%, four mascots peek from corners, cyan mist particles (≤20). Title **GHOSTLAP** (Bungee 128) ignites letter-by-letter (600ms), a ghost-cyan echo of the word lags 12px behind. Two dominant buttons (chamfered 360×72): **PLAY MULTIPLAYER** (gold→ember fill, dark text) and **RACE YOUR GHOST** (translucent cyan, cyan outline, shows personal-best subtext). Secondary row: History · Leaderboard · Watch · How to Play · Settings (Outfit 600 caps, 44px height). Guest chip bottom-left: `Guest · Morro` with `Save profile` link; no signup gate. States: loading = candle lighting animation; returning player greeting; offline = Race Your Ghost still works, multiplayer disabled with reason.

### 5.2 Multiplayer Lobby
960px plaque. Top: room code (Outfit 56, selectable, 6 characters) + `Copy link` (toast "Copied"). Four **player slots** 200×260 chamfered: empty = dashed border, "Waiting…" pulse; filled = mascot head (96px), name, state chip (`READY` gold / `NOT READY` grey / `RECONNECTING…` amber), host crown. **Character picker** row: four mascot buttons 88px; taken ones dimmed with owner's chip; selected gets gold rim. Controls: `READY` toggle; host-only `START` (enabled when ≥2 players, all ready; pulses). Chat/emote strip (4 quick emotes + short text). Spectators in a distinct slim list with an eye icon and grey-cyan tint. `Leave` top-right with confirmation. States: reconnect overlay "Reconnecting… 0:24"; room full; invalid code; host left → crown transfers animation.

### 5.3 Live Arena
Arena full, HUD per §4. Countdown 3-2-1-GO (Bungee 220, ember→gold) over frozen arena; characters spawn with a 300ms ring ripple. Final ten seconds: timer turns `--ember` and pulses; edge vignette 12%. Pause (solo only) dims arena; multiplayer help opens non-pausing overlay. Disconnect: pip amber + toast; opponents that drop become desaturated with "…" bubble.

### 5.4 Results
Winner stage: enlarged mascot (320px) on a lit dais, name in Outfit 72, score count-up 900ms. Ranking list 1–4 (chamfered rows, own row highlighted). Score breakdown bars per player: **Movement · Candles · Bursts/Hits** (stacked, labelled numbers). Confetti = 40 ember sparks maximum. Actions: `REMATCH`, `BACK TO LOBBY`, `MENU`. Solo ghost results: no ranking, no "WIN" language; shows "Personal best / New best" and `Save attempt`.

### 5.5 Ghost History
Grid of **history cards** (320×200): mascot head, date, score (Outfit), duration, pin star, branch count; badge `ORIGINAL` (cyan) or `REWRITE #n · from 0:38` (gold). Rewrites nest as an indented chain under their source. Actions: `Watch`, `Challenge`, `Rewrite From…`, `Pin`, `Share` (if permitted). Sort: Newest / Best / Pinned. Personal best has a gold laurel mark. Empty: stone tablet "No echoes yet — play a solo run." with `Start Solo`.

### 5.6 Ghost Challenge
Both characters spawn at the identical historical spawn. Ghost = §6 ghost renderer. Top-centre **compare HUD**: `PAST YOU 1,164` (cyan) | timer | `YOU NOW 1,211` (gold) with a delta pill `+47 AHEAD` (green) or `−32 BEHIND` (red). Same elapsed time alignment. Ghost never collects living candles; its collection pops are dim cyan "memory pops". Pre-start card: "Beat your 1,420 run" (real value) with `Start`.

### 5.7 Replay / Timeline
Arena renders full historical state; **Timeline Dock** below. Play/pause, scrub, markers, source chip, `REWRITE FROM HERE` (visible only when paused at a valid moment). A cyan **breadcrumb path** of the whole recorded route is drawn across the arena with 5-second time ticks. Invalid/final-tick: button disabled, tooltip "Nothing left to rewrite here." Confirm overlay: "Inherit at 0:38.2 → Score 812 · Charge 3 · 9 candles consumed" with `Cancel` / `Rewrite`.

### 5.8 Branch Comparison
Hero composition: huge signed **delta** (Bungee 160, green) "+158" centred over a **Path Divergence panel** (left 60%): arena thumbnail showing the cyan original route and gold new route splitting at a fork marker `FORK 0:38`, with the missed-candle location ringed. Right 40%: dual score curves (SVG; cyan solid, gold solid), divergence area tinted by sign, fork rule. Below: final score pair `1,420 → 1,578` (real values), category deltas (Movement / Candles / Bursts). Actions: `CHALLENGE AGAIN`, `REPLAY`, `SAVE`/`SAVED ✓`, `HISTORY`. Saved alternate appears in History as a branch.

### 5.9 Leaderboard / Profile
Tabs `MULTIPLAYER` (rated) and `SOLO BESTS` (clearly labelled "Not rated"). Rows: rank, mascot head, name, rating, W–L, recent results (5 pips). Profile plaque: guest/registered state, favourite mascot, recent matches. Solo bests appear as personal-best cards; ghost/rewrite attempts never inflate rating (copy states this).

### 5.10 Watch / Spectator
List of live rooms (players' heads, time left, spectator count). Spectator view = Live Arena minus controls; HUD adds `SPECTATING` eye badge; no Burst button; follow-player toggle highlights one mascot. Permitted solo replays reuse Replay without `Rewrite`. States: nothing live (stone tablet), private ("This echo is private"), room ended, disconnect.

### 5.11 Supporting overlays
Onboarding (3 cards: Move · Collect · Burst, with a looping micro-animation each), Settings (volume sliders, mute, reduced motion, controls hints, left-handed mobile), Share/copy toast, error banners, loading candle, disconnect/reconnect overlay, portrait prompt, replay permission denied, empty history.

---

## 6. Components, Visualization & Asset Language

### 6.1 The four mascots (heads only, drawn ~160px, used at 44–48)
| | **Morro** — cracked skull | **Wisp** — flame spirit | **Hexwick** — carved pumpkin | **Vesper** — bat |
|---|---|---|---|---|
| Personality | Reckless showman | Quick, mischievous | Grumpy, loveable bruiser | Cunning, competitive |
| Silhouette | Wide rounded skull, jaw notch | Teardrop with flickering tip | Squat wide oval with stem nub | Inverted triangle with two tall ears |
| Face | Round sockets with ember pupils, zig-zag grin, forehead crack | Huge oval eyes, tiny smile | Triangular eyes, jagged grin | Slanted angular eyes, fang grin |
| Motion read | Pupils lead direction; grin widens at speed | Body stretches/tapers along heading | Squashes on turns | Ears rotate toward nearest threat |
| Colour-blind distinction | Lightest, white-ish + ember crack | Teardrop shape | Widest silhouette | Ears (only horned silhouette) |
| Stage 1 | Crack warms ember | Tip glow | Eyes light | Ear tips glow |
| Stage 2 | Crack bright, outline rim warm | Body inner glow | Grin glows | Ear-and-eye glow |
| Stage 3 | Flame tuft on crown | Double tip | Stem sprouts flame | Ears edge fire |
| Stage 4 Overlit | Skull burns white-gold, eyes white-hot, crown of 3 flames | Becomes a tall flame, eyes white | Pumpkin cracks open with light | Wings of light behind ears |
| Ghost | Pale cyan, translucent, crack = cyan, same silhouette | same | same | same |
Each mascot is **one sprite sheet**: idle, move-left/right, blink, hit, burst-wind-up, win, lose (heads reuse expressions via swapped eye/mouth sprites).

### 6.2 Arena materials
Floor tiles ×4 (plain, cracked, rune, worn), wall segments (top, face, corner, end-cap, torch bracket variants), skull relief ×4, torch bracket ×8, Vigil Circle rune ring decal, spawn crest decal ×4. All in one atlas (2048²) plus a tiled 256² floor texture.

### 6.3 Candles
Silhouette: tapering wax stub, flat base, teardrop flame. **Telegraph (1.2s):** thin dashed `--flame` ring contracting from r=48 to r=36 with 6 spark dots. **Appear (180ms):** wick pops up, flame ignites with a scale-bounce. **Idle:** flame flicker (2-frame swap 160ms), soft halo radius 56 at 30%. **Expiry (last 2s):** flame shrinks, flicker accelerates, ring turns grey; fade 300ms. **Collected:** scale 1.25 → burst of 8 sparks flying toward the player's charge ring, `+N` popup.

### 6.4 Soulfire & Burst
- **World indicator:** 4-segment ground ring beneath player; each segment lights with `--soulfire`.
- HUD: four diamond **sockets** (32px) with fill animation 220ms; at 4/4 all sockets blaze and the label `BURST` pulses.
- **Overlit:** 1.4× additive corona, rotating rune ring at feet (rotation 0.6 rps), subtle heat shimmer (displacement, optional), eye whites.
- **Burst:** anticipation 280ms squash + ring contracts; expansion 520ms ring r:44→420 (3 layers: thin bright ring, soft band, rune-textured disc); valid target flash 120ms white + sparks; aftermath 400ms fade; sockets reset to 0.
- **Contact:** stronger→weaker: weaker flashes white + `−N` popup + 4 sparks; equal charge: both "tink" neutral grey spark, no damage.

### 6.5 Ghost renderer
Same mascot sprite; tint `#7FE8FF`, alpha 0.55, no shadow, 2px additive cyan rim, dashed cyan ground ring, 6-copy trailing afterimage at 0.25 alpha. **Z-order: floor → ghost → living player.** Overlap rule: the ghost always draws a 3px cyan **rim halo scaled 1.12** behind the living sprite so both read even when 100% overlapped; never offset positions.

### 6.6 HUD & menu pieces
Chamfered stone plaque, rune button (primary gold, secondary cyan, tertiary outline), slot card, history card, emote chip, toast, tablet empty-state. Buttons: 72px primary / 48px secondary.

### 6.7 Reusable effect primitives (PixiJS)
`SparkParticle`, `RingPulse`, `TrailCopy`, `AdditiveGlow`, `HitFlash`, `PopText`, `ScreenVignette`, `RuneRing`. All other effects are compositions of these. Pool objects; cap 50 particles.

---

## 7. Exact Interaction & State Language
| Event | Duration | Easing | Layers | Sound intent | Reduced motion |
|---|---|---|---|---|---|
| Candle telegraph | 1200ms | linear | ring + sparks | soft rising chime | static ring, no sparks |
| Candle collect | 260ms | ease-out-back | sparks, pop | bright warm "ting" | pop only |
| Charge +1 | 220ms | ease-out | socket + ring | ascending pitch per stage | instant fill |
| Overlit onset | 500ms | ease-out-expo | corona, rune ring | low swell | static glow |
| Burst wind-up | 280ms | ease-in | squash, contract | inhale hum | 100ms flash |
| Burst expand | 520ms | cubic-bezier(.1,.8,.2,1) | ring layers | deep boom | single ring fade 200ms |
| Hit | 120ms flash + 400ms | ease-out | flash, sparks | crisp clack | flash only |
| Final 10s | 1000ms loop | ease-in-out | timer pulse, vignette | ticking | colour only |
| Win | 900ms | ease-out | ember sparks | fanfare | static glow |
| Defeat | 600ms | ease-in | desaturate 40% | sigh | same |
| Button hover | 120ms | ease-out | +6% glow | tick | none |
| Button press | 80ms | ease-in | scale .97 | thud | none |
| Disconnect | instant | — | pip amber, bubble | low blip | same |
| Reconnect | 300ms | ease-out | pip green flash | up-chime | same |

**State vs. cosmetic:** state changes = charge, score, candle consumption, burst resolution, rewind fork. Cosmetic = mist, flame flicker, rune pulse. Cosmetic effects never overlap gameplay-critical readability and drop first when frame time degrades.

Focus: 3px cyan ring with 2px void gap. Disabled: `--disabled`, tooltip stating reason. Loading: lit-candle spinner. Confirmations: `Cancel` left / action verb right; leaving a live match requires confirm.

---

## 8. Timeline / Restore / Branch / Compare UX

**The Dock — "Wax Fuse."** 96px stone slab, bottom-anchored, chamfered 12. Left → right: rune **play/pause** (48px) · prev/next marker · **source chip** `ORIGINAL RUN · Morro · 1,420` (cyan) or `REWRITE #2 · from 0:38` (gold) · **Fuse Track** · time `0:38.2 / 1:30.0` · **`REWRITE FROM HERE`** · `Exit`.

**Fuse Track:** cream wax line (remaining), charred dark line behind the flame (elapsed). **Playhead = flame diamond (24px)** with a soft warm glow. **Event markers** above the line: ▲ flame glyph = candle collected, hollow ring = candle missed (expired), ◎ = Burst, ✦ = contact. Missed-candle markers pulse once when the playhead nears them. **Fork point** = a cyan **tuning-fork notch** cut into the track, label `FORK 0:38`. Seeking: drag or click; arrow keys ±0.1s, Shift ±1s, `,`/`.` previous/next marker, Space play/pause, Enter rewrite, Esc exit. Left = earlier.

**On scrub:** arena reconstructs exact state: positions, candle states, consumed candles (dark wick, ash), charges, scores; HUD mirrors recorded values. During drag, a cyan time-ripple (RingPulse, 300ms, one per 200ms) emanates from the player's position; consumed candles light up when scrubbing backward (reverse ember: 6 sparks drift *down* into the wick).

**Branch eligibility:** any moment with full recorded state, except the final tick and the invalid span near the end; those track areas are cross-hatched; button disabled with explanation. Multiplayer histories never offer rewrite.

**Rewrite transition (1.8s, never obscures the arena):**
1. 0–200ms: arena desaturates 40% and tints cyan; all UI quiets.
2. 200–700ms: reverse-ember return of candles consumed after the fork to their wicks (those state changes are real).
3. 700–1100ms: **Split.** The player's sprite duplicates; the cyan copy slides out 30px with elastic ease, a thin cyan RingPulse expands, and a tuning-fork glyph hovers `FORK 0:38`.
4. 1100–1500ms: colour returns; countdown "3-2-1" is skipped; a 1s ready beat.
5. 1500–1800ms: Dock transitions to Challenge HUD; both start moving.

**Divergence visualization:** living trail warm gold, ghost trail cyan; when separated >160px a dotted bridge shows with floating delta tag; HUD pill updates at identical elapsed time.

**Comparison:** end screen as §5.8; fork rule consistent; scores real.

**Integrity:** persistent rune stamp on Replay/Challenge: `ORIGINAL RUN PRESERVED`. A rewrite is a new branch with its own ID; the source card remains unchanged in History.

**Cross-app correspondence**
| Stays analogous | Transformed here |
|---|---|
| Bottom dock, left=earlier, playhead, markers | Wax fuse, flame diamond, flame/ring glyphs |
| Source chip, branch ID, fork marker | `ORIGINAL RUN`, `REWRITE #n`, tuning-fork notch |
| Play/pause, step markers, Enter to branch | Same keys, rune buttons |
| Original = ghost-blue | Ghost cyan; alternate = living gold |
| Original never changes | "Original run preserved" stamp |
| Compare + signed delta | Giant Bungee delta + route split |

---

## 9. Demo-First Hero Interaction Sequence
**G1 — Hero still: Live Arena.** Whole arena, four mascots, 16 candles (4 telegraphing), one Overlit mascot with corona, HUD plaques in top band, timer 0:47.
**G2 — Solo run history.** Ghost History card for the saved 90-second run, cyan `ORIGINAL` badge, score in Outfit. Click `Watch`.
**G3 — Replay.** Arena in historical state, cyan breadcrumb path across the floor; a missed-candle hollow ring marker on the Fuse at ≈0:38 pulses.
**G4 — Scrub back.** Drag playhead; ripples; consumed candles relight with downward embers; arena visibly returns to exact historical appearance; HUD score drops to recorded value.
**G5 — Rewrite From Here.** Press button → confirm card ("Inherits 0:38.2 · Score · Charge · Candles") → Rewrite → §8 transition.
**G6 — THE SPLIT (signature Timeline still):** Frozen composition: left half — solid mascot (warm halo) heading to the missed candle; right — cyan ghost continuing old route; cyan and gold trails diverge from the glowing fork ring; fork-glyph `FORK 0:38`; top-centre HUD `PAST YOU 812 | 0:52 | YOU NOW 868 | +56 AHEAD`; Dock below with tuning-fork notch and gold `REWRITE #1` source chip. Visual weight: two characters 35%, trails 20%, HUD 20%, dock 15%, arena 10%.
**G7 — Finish.** Branch Comparison: giant `+158` (real delta), route-split panel, dual curves, `SAVED ✓` state, `CHALLENGE AGAIN`.
**G8 — Handoff.** Menu → Watch; judges now know the interaction before Revenant's dock appears.

Judges must understand without narration: a mistake, a rewind, a second path, a measurable improvement, the original untouched.

---

## 10. Accessibility, Responsiveness & Failure States
- Text contrast ≥ 7:1 on plaques; HUD text on dark `--surface` plaques, never directly on arena.
- Identity never by colour alone: silhouette + face + name label chip over heads in multiplayer; ghost = alpha + rim + dashed ring.
- Keyboard: Tab/arrow navigation in menus, Enter/Space activate; gameplay WASD/arrows + Space; replay keys per §8. Touch ≥48px.
- Reduced motion option (Settings + `prefers-reduced-motion`): replaces particles with single-frame flashes, no screen shake/vignette pulse, rewind becomes 400ms cross-fade with a still Split frame.
- Sound alternatives: every audio cue has a visual counterpart; mute is global; captions for fanfare not needed.
- Failure states: lobby reconnect (30s timer), match connection lost (pause local rendering, "Reconnecting…", then "Return to menu"), replay unavailable/private tablet, empty history/leaderboard/watch tablets, room full, invalid code, WebGL unsupported (message, no degraded canvas).
- Privacy indicators: eye icon for public replays, lock for private; share requires explicit toggle.
- Never hide critical info behind hover; tooltips mirror inline captions.

---

## 11. Implementation-Ready Frontend Design Rules
1. Fonts: Google Fonts CSS2 `https://fonts.googleapis.com/css2?family=Bungee&family=Outfit:wght@500..800&display=swap`; use `"Bungee", Impact, sans-serif` for display and `"Outfit", system-ui, sans-serif` for functional UI. Bungee is weight 400 only; disable synthetic bold/italic (`font-synthesis: none`). Use `font-variant-numeric: tabular-nums` on changing Outfit timer, score and timeline values. Keep the two-family selection centralized for easy later replacement.
2. Tokens as CSS variables on `:root`; **no theme toggle**.
3. React renders menus, HUD, overlays; PixiJS renders only the arena. HUD reads from game state store, never from sprite properties.
4. Single 2048² texture atlas for arena + mascots + VFX; tiled floor texture; load before the countdown; show lit-candle loader.
5. Design-asset adapter: all sprites referenced by semantic keys (`mascot.morro.idle`, `fx.ring`) so placeholder circles/rects can be swapped for final art without touching logic.
6. Ghost rendering: separate container below the living container; tint + alpha + outline sprite; no collision, scoring or event hooks attached.
7. Particles: pooled, ≤50 active; additive blend only for flame/Soulfire; reduce to 25 under 45fps.
8. Scaling: world container scaled uniformly, DPR-capped at 2; HUD positioned in world-rect coordinates.
9. Timeline Dock: reuse the shared `<TimelineDock>` props contract (`span, markers, source, forkAt, branches, onSeek, onFork`) with a Ghostlap skin.
10. Every effect must be triggered by a real game event; decorative loops limited to flame flicker, mist, rune pulse.
11. Component libraries must not impose default styling; build components from tokens.

---

## 12. Design Acceptance Checklist & Forbidden Patterns
**Checklist**
- [ ] Full 16:9 arena always visible, never cropped; letterbox correct.
- [ ] Four mascots distinguishable by silhouette in greyscale at 44px.
- [ ] In active play, own position, available candles and higher-charge/Overlit rivals are identifiable immediately.
- [ ] Remaining time, current score and exact Soulfire charge remain legible during play.
- [ ] Soulfire stages 0–4 readable at a glance; Overlit unmistakable.
- [ ] Ghost clearly translucent cyan, rendered under the living player, rim visible when overlapped; past and living player distinguishable even during overlap.
- [ ] Past/now score HUD time-aligned with signed ahead/behind delta; fork point and branch identity clear during rewind/rewrite.
- [ ] Rewind transition shows the Split and never implies history was altered.
- [ ] Timeline Dock bottom-anchored, same controls/keys as Revenant.
- [ ] All ten required screens plus supporting overlays implemented.
- [ ] Only Bungee + Outfit, assigned consistently by §3.1; dark only.
- [ ] Mobile landscape controls don't overlap candles/spawns; portrait prompt present.
- [ ] Reduced-motion and mute work.
- [ ] Solo results never shown as rated wins.

**Never**
- Light mode, neon/cyberpunk glow, pixel art, pseudo-3D camera, cropped or tracking camera.
- Four identical circle heads, dot-eye blobs, pumpkin clip art, stock Halloween fonts.
- Unrequested mechanics: weapons, power-ups, bombs, hazards, knockouts, stuns, skills.
- Ghost that collides, steals candles or affects score; multiplayer replay/rewrite.
- Hardcoded sample scores shown as results; ranking solo ghost runs.
- HUD covering the play area; decorations that look like pickups.
- Reversed gameplay physics or any suggestion the original run changed.
- Hover-only information, colour-only meaning, particle counts above the cap.
