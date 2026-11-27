# Spec 13.3: the four heads and their acting vocabulary

**CREATIVE IMPLEMENTATION DONE · HUMAN REVIEW PENDING · TECHNICAL ACCEPTANCE PENDING.** Existing `web/apps/ghost-lap` only. No Marrow, rules, transport or DTO change. The types here are presentation types, not server DTOs.

## Try it
In `web/apps/ghost-lap`: `npx vite --port 5174`, then `http://localhost:5174/?screen=characterStudy` (dev only; also the "Character study" button in the preview strip). Hot reload applies.

## Files
| File | Role |
|---|---|
| `scripts/build-character-atlas.py` | Builds `public/game-assets/generated/characters.png` and `src/config/characterAtlas.json` from the real Kenney **Double** individual PNGs. The source folder name ends in ONE SPACE and is spelled and asserted explicitly. Source files are only read. |
| `src/characters/expression.ts` | Pure expression selector (`ExpressionState`): charge face, 2 s fear and Burst-hit overrides, open-ended release, winner, ghost, de-duplication, `gazeVector`, `winnerSlots`. No Pixi, no clock. |
| `src/characters/characterSpec.ts` | The four slots, per-shape face fit, halo alphas, gaze travel, and every face as a list of source pieces (`FACE_RECIPES`). All numbers are visual tuning, not game truth. |
| `src/characters/CharacterView.ts` | ONE reusable head (shadow, halo, upright head, crossfading face rigs). The same class is used for living and ghost heads. `head` is the container a later spectral filter attaches to. |
| `src/characters/CharacterAssets.ts` | Atlas loader (ref-counted, so StrictMode double-mount cannot unload a live texture). |
| `src/screens/CharacterStudy.tsx` | The development expression viewer. |
| `test/expression.test.ts` | Ten focused tests for the expression rules (`npm test`). |

## How a scene uses it
```ts
const textures = await CharacterTextures.load();
const view = new CharacterView(textures, { slot, kind: "living" | "ghost" });
world.layers.actors.addChild(view.root);
// every frame, from authoritative facts only:
view.setPosition(x, y); view.setCharge(charge, now); view.setGaze(heldX, heldY);
view.update(now, dtMs);
// authoritative events, once each (key = MatchEvent.Key or any stable id):
view.expression.cue("fear", now, key);   // ContactDrain, ONLY for the weaker victim Slot
view.expression.cue("hit", now, key);    // BurstHit, ONLY for the victim Slot
view.expression.beginRelease(now, key);  // BurstManual / BurstAuto for the releasing slot at charge 4
view.expression.setWinner(true);         // finalized result, every rank-1 slot (ties crown all)
```
`kind: "ghost"` ignores all of the above and always shows the evil face. The later `SpectralGhostEffect` goes on `view.head`.

## Source mapping (what is pack art and what is derived)
Full list with reasons: `meta.derivations` in `characterAtlas.json`. Summary:

| Face part | Source |
|---|---|
| Bodies | `yellow_body_circle` (P1, hue-rotated -13° at atlas build time as `body_p1_orange_derived`, Option C), `pink_body_square`, `yellow_body_rhombus`, `green_body_squircle`. Kenney files unchanged. |
| Eyes (open) | `facial_part_eye_open`, split into white eye + ink pupil by alpha so only the pupil moves (derived crop of the same pixels) |
| Closed eyes | `facial_part_eye_closed_down` (release crease), `_up` (winner arc). Untouched. |
| X eyes, slim brow | connected components of `face_j` |
| Heavy worried brow | connected component of `face_i` (flipped vertically at runtime for the tense release brow) |
| Brows | `facial_part_eyebrow_a` (level), `_b` (gentle slant) |
| Mouths | `mouth_sad`, `mouth_happy`, `mouth_smirk` untouched |
| Ghost eyes and brows | left unit of `face_f` (right is its mirror) + `mouth_smirk` |
| **Flat neutral mouth** | DERIVED: `eyebrow_a` resampled to the stroke weight of `mouth_happy` (the pack has none) |
| **4/4 grin** | DERIVED: `mouth_happy` x1.45 (the pack has no broader smile) |
| **Release oval, fear oval** | DERIVED: the pupil ink disc scaled to an oval |
| **Winner open mouth** | DERIVED: lower half of the pupil disc, widened |
| **Burst-hit mouth** | `mouth_smirk` flipped and tilted (short, crooked, downward) |
| Halo, shadow | computed from each body's own alpha (dilate + feather / blur), tinted and offset at runtime |
| Orange option C | `yellow_body_circle` hue rotated -13 degrees. COMPARISON ONLY, not wired to any slot. |

`shadow.png` of the pack was not used: it is a flat oval at 10 percent alpha made for side-view platformers. A silhouette-following shadow was derived instead (skill ref 04 allows a tiny generated texture).

## Behaviour decisions
- 0 and 1 are the same frown with no halo; HUD numeric charge is outside this component and stays exact.
- Halo alpha 0, 0, 0.08, 0.14, 0.28 (ref 06 seeds), hue-matched, silhouette-shaped, no filter. During a release the halo is emphasised (x1.7, capped 0.5).
- Gaze: pupils only, 8 directions, diagonals normalised, last non-zero direction kept while stationary, ~55 ms glide. Heads never rotate. Fear, X-eyes, release, winner and ghost faces have no moving pupils.
- Overrides: latest real event wins; at the same instant hit beats fear. Each lasts 2000 ms of visual wall time (characters.md). Expiry returns to the CURRENT charge face. Repeated key = no restart.
- **Release has no invented length.** It lasts until `endRelease()`, until an explicit `untilMs` derived from real data, or until the authoritative charge changes in a LATER update than the release began (same instant counts as part of the release). The backend/skill leave the visual lifetime open.
- Crossfade 130 ms between calm charge faces, 40 ms for any reaction.
- **Head size policy (human-approved).** Preferred visual head size is **64 world units**, the default in the viewer and gameplay presentation. Fallbacks, used only if real gameplay shows unacceptable wall overlap, occlusion or visual-collision mismatch: **56** first, **48** (collider-matching) second. The authoritative backend collision body stays **48x48**, unchanged. This is a visual-size policy only. Final acceptance of 64 near walls is pending real gameplay observation. All three options live in `HEAD_SIZE_OPTIONS` (`characterSpec.ts`); `HEAD_UNITS` is the first entry.

## Needed from others (exact)
1. **Release lifecycle (backend/event owner).** `ContactDrain`, `BurstHit` and `BurstManual/Auto` carry a tick but no visual duration for the release. Smallest decision: either a real window (for example "face lasts N ticks after the cue") or confirmation that "until the charge drops" is right. Creative slice continues; the API takes either.
2. **Winner mapping.** `winnerSlots` treats every rank 1 row as a winner. Confirm against `MatchPlayerResultView.CompetitivePlacement` and `Outcome` that a forfeit can never hold rank 1 when finishers exist. Solo `FinalResult.Winners` is not published on the live wire.
3. ~~P1 colour decision~~ **Decided: Option C.** P1 uses the -13° hue-rotated derived orange circle (`#fa8e39`) in every runtime use (`SLOT_SPECS[0]`). It is derived in `scripts/build-character-atlas.py` from the unchanged `yellow_body_circle.png`, with no runtime filter or shader. A (unmodified yellow) and B (red) stay in the viewer comparison, labelled not selected. P2 pink, P3 yellow, P4 green are unchanged.
4. Spectral material of the ghost, night grade, and effects are later tasks (13.5/13.6).

## Checks run
`npm test` (10 tests incl. a 200-sequence randomised invariant), `tsc`, `vite build`, `npm run check:bundles`; headless Chromium (software GL) against the Vite server at 1600x900, 1280x720 and 1100x760: no console errors, heads render, timed cues observed (hit and fear return to the current charge after about 2 s; hit then fear then hit resolves to the latest; release holds and ends on a later charge drop). Not run: GPU/FPS, Firefox/Safari, real multiplayer or solo frames, existing Playwright specs.
