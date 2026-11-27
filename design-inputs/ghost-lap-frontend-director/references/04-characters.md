# 04 — Character visual implementation; read real `characters.md` first

**Source authority:** `$GAME_ASSETS/characters.md` (full user-authored details). This reference is the renderer's implementation mapping, not a replacement. The source file explicitly says no other animation details are defined — therefore do not invent walking arms, legs or attack animations.

## Four exact head-only identities (CONFIRMED)

| Slot | Base | Colour | Shape treatment |
|---|---|---|---|
| P1 | circle | orange | perfect softly shaded round head |
| P2 | square | pink | squarer head with mildly softened corners |
| P3 | diamond | yellow | diamond with four cardinal points |
| P4 | rounded square | green | broad soft corners, distinct from pink square |

All bodies and faces come from `kenney_shape-characters/`. Characters are **heads only**; no hands/limbs/legs. Head does **not rotate with heading**; directional gaze moves pupils only. Position based on actual backend coordinates. Colour and silhouette do not change with charge or expression. Players can have different heads but use same expression semantics.

## Drawing hierarchy — one `CharacterView` instance per living player

```text
LivingCharacterView (Pixi Container, authoritative screen position)
├─ GroundShadow: tiny soft oval slightly bottom-right, low alpha, under everything
├─ ChargeHalo: derived silhouette/soft aura behind shape, hue linked to player's body colour
└─ CrispHead:
   ├─ BaseShape: chosen Kenney body texture
   └─ FacialFeatures: eyes/eyebrows/pupils/mouth as reusable sub-sprites
```

- Facial features must remain distinct at actual on-screen size; do not filter the whole `LivingCharacterView` with Glow/Bloom/Blur.
- The halo may be a precomputed translucent silhouette sprite, a small cached mask-derived texture, or soft outline sprite behind base head; not a bright body colour replacement. Use one texture/shape family cached for same body silhouette. No 60fps generating textures.
- GroundShadow uses a reusable soft oval asset or tiny generated texture; offset bottom-right simulates light from top-left. A world lightmap doesn't dynamically reshape it unless artistically essential.
- Neutral or normal faces use pupils pointing left/right/up/down based on *actual movement direction*. If stationary, keep last valid facing or default based on verified design; do not tie gaze to candle spawn or travel destination. Do not spin head.

## Charge expression mapping — explicit latest user correction (CONFIRMED)

| Authoritative charge | Facial state | Halo appearance |
|---|---|---|
| 0/4 | **frown** | none |
| 1/4 | **same frown as 0/4** | none |
| 2/4 | **neutral** | extremely faint, coloured to character |
| 3/4 | **happy small smile** | slightly stronger, still faint |
| 4/4 | **super happy grin** | noticeably stronger 'armed' halo, but less luminous than a candle |

**0 and 1 must not be merged mechanically**: the numeric Soulfire HUD and backend still distinguish them. Only *expression and halo* are identical. Do not assume gameplay begins at 0; source `characters.md` once says players begin at 1/4, which is not an authoritative frontend state. Render actual charge from backend.

The expressions are about **emotion progression**, not body scaling. Charge stage changes are instant and readable but can crossfade facial components over a brief restrained DIRECTOR BASELINE interval (100–160ms) if it doesn't obscure server timing. No particles, screen flashing or cartoon squashing required.

## Special expressions — priority over power face

1. **4/4 energy release:** eyes firmly squeezed shut in curved creases, tensed inward brows, simple wide dark open oval mouth (screaming exertion). Only when actual 4/4 charge-release event happens. Duration **VERIFY with backend/event lifecycle**; do not invent 2 seconds.
2. **Actual Burst hit victim:** X-eyes, pained crooked mouth and knocked-out brows; **2 seconds** visual override **only** if blast hit is authoritative. No body deformation, blood, glitch or additional RGB FX.
3. **Lower-power collision victim:** wide frightened eyes/small pupils, inward-raised brows, small worried open oval/tight mouth; **2 seconds** only on the **lower-charge** participant in authoritative unequal-power collision. Higher-power participant retains current expression; do not trigger for all contacts.
4. **Winner:** joyful closed-eye arcs and broad triumphant smile distinct from 4/4 grin. Use only on result-confirmed winner(s) as backend defines. Ties require reading actual placement logic; don't arbitrarily crown one.
5. **Ghost:** permanent mean/evil eyebrows/narrow eyes/crooked smirk for its entire night gameplay appearance. **Never use historical power-based facial changes for spectral ghost** even if its recorded source had different original expressions.

### Expression priority rule for frontend agent

**DIRECTOR BASELINE when two server-visible events overlap:** result-terminal winner (on results) > actual hit/collision 2s override (latest valid authoritative event; do not restart from duplicate) > release-face while active > normal power expression. If overlapping hit/collision events have different priorities not specified by authoritative source, choose latest real event locally only for rendering and note the choice. On expiry return to the **current** authoritative charge expression, not the old frozen face. The ghost's evil expression is independent of living expression stack.

If the backend emits a hard `expression` field or reaction expiry, that authority overrides local precedence. Do not equate '2 seconds' with 60 historical ticks unless backend explicitly defines time basis; UI duration is visual wall time unless backend says otherwise.

## Gaze specifics

`MovementDirection` -> pupil offset within white eye clip. Four cardinal directions; diagonals resolve via actual last orientation or normalized vector, **VERIFY** actual input representation. Keep dark pupils contained within whites and never move eyebrows with movement. Hit/release/ghost expressions override pupil travel as appropriate. Reuse same face geometry per head, normalize relative to head width/height, then test on diamond tip and green rounded square.

## Living/ghost overlap

Ghost is a **separate display object** rendered below living player and above world ground/lighting as chosen. The living player should visually cover ghost body at identical coordinates. A faint cyan edge outside living silhouette may remain visible. Never deliberately offset ghost by a pixel or force separation. Genuine later divergence comes from historical snapshot vs new player position only.

Ghost material uses one `SpectralGhostEffect` on a composed **head + permanent evil face**. Luminance maps orange/pink/yellow/green to cyan/white while preserving dark feature definition. Ghost is translucent but not invisible, has light edge emission, **no internal/body blur**, no lightmap illumination and no gameplay collisions. Exact alpha/edge values are visual preset defaults, see `06`.

## Acceptance at four screen sizes

Render all four at gameplay scale against day snow, night snow, walls and candlelight; screenshot each at 0,1,2,3,4. Verify pink square vs green rounded square shape distinct, diamond facial alignment correct, no facial components cut by clipping, 0 and 1 identical visuals, 2 vs 3 vs 4 distinguishable. Verify hit 2 seconds, collision only on weaker, 4/4 release expression and correct winner state on real results.
