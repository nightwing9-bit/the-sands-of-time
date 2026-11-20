# 08 — The Sands of Time: signature hourglass, timeline, rewind and fork

This is the **flagship interface**. It must visibly connect the game to the same engine concept shown in the banking application: **hourglass anchored to the LEFT of horizontal bottom history track; a precise fork marker; real time navigation and evidence of two futures**. But it must feel like Ghost Lap's ethereal frozen world, **not a copied banking dashboard or Alto screen**.

## 1. Hard functional truth (CONFIRMED from backend inventory)

- Original history: **2700 transitions at 30Hz; T0..T2700** (~90 seconds). Scrub a verified original to **any** tick T0..T2700. New fork allowed **T0..T2699** only; T2700 inspection-only.
- Scrubber selects **real recorded frames**. Never synthesize intermediate historical events/physics; label actual tick and elapsed time. A fork at T inherits the entire original state and changes decisions from T+1; original never mutates.
- Replay play/pause is local **viewing**, not live server pause. Historical candle positions, charge, scores and event markers shown only from real archive. Original score at current selected tick is NOT the final score.
- Saved future Challenge or Fork child inherits a parent/source; no nested child fork. Original and child comparisons align on **absolute tick** and show signed score delta at same time. A full replay at T2700 is valid even though fork there is not.
- Drag a **loaded history** using local frames/cache; do not query database on every pointer movement. Backend may provide preloaded samples; interface uses actual contract.

## 2. Signature visual composition (DIRECTOR BASELINE)

Create a bottom contextual **SandsOfTimeTimeline** overlay inside the arena layout only in history-inspection/replay, rewind/fork selection and branch comparison. It should be absent from main menu, lobby, normal multiplayer and normal solo recording.

**Left compartment (iconic):** one elegant **2.5D transparent hourglass**, glass outline with carefully shaded translucency/refraction, softly warm golden sand and subtle falling-sand motion. Not the Pinterest image file; draw original artwork using SVG/Canvas/pre-rendered legal art. Prefer self-contained SVG/HTML art to bulky 3D dependencies. Hourglass visual height about 74–100px for 900px-tall design board; centre at left of track, high contrast on dark frost. A few gentle sand grains are enough, not GPU particle storm. Reduce/stop decorative falling sand under reduced motion. The hourglass does **not** control authoritative time itself.

**Center compartment:** horizontal history rail with rounded thin track, `T0` at left and `T2700` at right, one larger glowing but restrained **fork diamond** marker at selected eligible tick; row of subdued real event pips if event data exists. Sand-gold selected point, pale icy-blue original time, ghost cyan for already-created alternate branch. Keep enormous empty white labels out of track.

**Right/adjacent controls:** discreet actual time/tick readout, play/pause historical viewing, previous/next step, `Fork Here` (only eligible original T0..T2699), exit/back. On ghost challenge source inspection, direct `Challenge from Start` exists as a separate action. Do not invent faster-than-real-time history speed dropdown if not needed; view-only speed buttons can be an optional later enhancement.

**Layout example, no bank chrome:**

```text
┌────────────────────── translucent bottom rewind tray ────────────────────────────┐
│  2.5D     Original history        T0               selected time         T2700 │
│ [glass]   thin pale rail       ────────◆───────────────────────────────────────   │
│ [sand]    [← step] [▶ view]      selected 00:47.233  ·  tick 1417  [Fork Here]  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

This is a semantic layout illustration, not a real event or promised tick value. Must keep the on-screen arena visible above rail. Panel uses subtle slate frost, Source Serif 4 section title and Source Sans 3 for tick/data. Avoid thick blue finance-UI navigation bars, corporate chart legends, transaction badges, policy names and visual clutter.

## 3. Interaction details with no ambiguity

- Pointer: clicking rail seeks to nearest valid server tick within displayed history; dragging smoothly updates preview through already-loaded data; release commits exact selected integer tick; pressing `Fork Here` requests actual backend fork, not immediate success.
- Keyboard: focusable slider with `aria-valuemin=0`, `aria-valuemax=2700`, integer value; Left/Right move a single tick by default, Shift+Left/Right step by a convenient one-second **30-tick VIEW step** (DIRECTOR BASELINE), Home goes T0, End T2700. `Fork Here` disabled at 2700. Keyboard controls may be adjusted to avoid conflict with live controls because history mode is separate.
- Track semantics: selected position marker and playback position marker are distinct when viewing; fork marker denotes *source fork tick*, not current chosen tick when comparing an existing child. Display both only when both meaningful. Support direct exact tick text entry **only if needed** for precision; the slider remains primary.
- Time readout format: `mm:ss.mmm` for user comprehension, derived from `tick / 30`, alongside tick ID in smaller copy (e.g. `T1417`); absolute ticks win if rounding to ms differs. Do not silently round to an unforkable T2700.
- If available, real candle/Burst event markers appear as small unobtrusive dots; clicking a dot seeks genuine tick. If events absent, render clean empty rail, not fabricated story beats.
- `Fork Here` is the one emphasized sand-gold CTA. Separate `Replay`, `Ghost Challenge from Start`, `Back`, `Compare` according to context. No giant mandatory modal for each scrub.

## 4. Initial original vs child comparison

Use original line as subdued ice/blue-grey. At the actual fork tick place distinct warm gold branch diamond. A second thin path emerges only *after* that marker, cyan-ish for child (or alternate slate for daytime historical compare). Children identified by type (`Challenge` starts T0; `Fork` starts selected T). In comparison view show two tiny aligned values — `Original  ____`, `Future ____`, `Δ +/- ____` — drawn from genuine score data. Not a full analytics dashboard or fake plotted curves. Historical samples may support a small authentic progression sparkline if agent can show it cleanly; not mandatory.

**Critical authenticity:** identical pre-fork history must visually be represented as shared, not two fabricated diverging tracks; child score/reference computed from actual same-tick samples. No smooth cartoon projection invented for real score curves. Ghost path can overlap living path exactly until real decisions diverge.

## 5. Rewind interaction and transition narrative

For a user forking at selected tick T:

1. **History inspect light context:** arena displays selected original frame; timeline shows T exactly; `Fork Here` enabled only if valid. User confirms action. If server says ineligible/unauthorized/unavailable, remain in clear error state; don't animate fake success.
2. **Optional history-speed rewind:** present an approx 1-second reverse **montage of real historical states**, time-compressed to selected T; adapt sample playback speed to interval. This visual target came from user. Never turn video frames backwards as a replacement for timeline truth. If data isn't ready, show actual preparing state first.
3. **Light scene at chosen tick:** show exact original state at T, alive character in their natural colours. Do not yet invent ghost separation.
4. **CRT disturbance**: brief on game view, designed as a supernatural state switch.
5. **Smoke transition**: imported `smoke_transition.png`, nominal 2.8 seconds; use screen-cover/neutral blend to swap environment to graded night, enable correct candle light treatment and spectral ghost under cover.
6. **Ready gate:** when backend confirms prepared/ready, reveal night gameplay at correct source tick+start semantics and launch night soundtrack + wind/knock cycle. If not ready after animation, maintain a visually matching truthful `Preparing history...` state. If failed, show retry/back; never use timeout to make up success.
7. **Living/new future:** player and spectral original ghost share exact history/position as actual data dictates. They separate visually **only if their actual decisions/positions diverge**.

**Timing note:** user's design target is roughly 1 second for rewind montage and roughly 3 seconds for light->night CRT/smoke, but backend preparation was observed around 5–6 seconds. These are separate concerns; do not promise a 3s end-to-end fork. Wait for authoritative readiness.

**Ghost Challenge (different flow):** new run starts from T0 with original ghost. Do **not** require a Fork Here or arbitrary rewind montage. It may use CRT/smoke night reveal after preparation; keep identity clear as `Challenge` rather than `Fork`. Original run recording itself remains daylight and no ghost.

## 6. Hourglass technical approach

- Build a small original vector component `HourglassMark.tsx` using SVG contours, translucent gradient glass and sand fill. Optional 2.5D illusion: front glass highlight, inner tinted edge, a subtle rear shade, warm top/bottom sand polygons and one short falling thread; no 3D model engine.
- If more photorealistic 2.5D artwork is later approved, swap the graphic behind the same component interface. The hourglass remains physically **to timeline's left** in all history contexts.
- Use CSS animation (or Canvas single ticker) only when timeline visible. Animation freeze on reduced motion or while not visible. Do not derive actual tick from animated sand level; if visual sand fill represents progress, it must track selected tick accurately.
- Ensure hourglass silhouette is recognizable even at 64px; avoid muddy glass blur and high-frequency shimmer.

## 7. Timeline visual acceptance

At a glance: judge sees **same engine signature** as banking counterpart (hourglass left, horizontal time, fork diamond) yet sees an elegant distinct frozen-game interpretation. At any tick, read the real selected time and current frame; at T2700 `Fork` unavailable with reason; at T0 exact start; dragging fast does not cause DB storms; results survive repeat/back/replay; no fake ghost diverging before true new decisions.
