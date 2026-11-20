# Spec 13.0 — approved source asset audit

18 November 2026, Asia/Kolkata. **PASS WITH LATER BLOCKERS** for the audit; not an asset-release or visual-implementation approval. Cross-reference [frontend/world/transport map](17-ghostlap-spec13-frontend-contract-map.md). B1 and 13.1–13.12 remain **NOT STARTED**.

## B1. Actual source root and execution

Verified accessible local root: `/Users/rroy/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/`. Read actual user-authored `characters.md` (5321 bytes), entire installed frontend skill/README/refs 00–12 and `scripts/asset_inventory.py`.

Executed read-only:

```sh
python3 design-inputs/ghost-lap-frontend-director/scripts/asset_inventory.py --root design-inputs/game-assets --json
```

**Exit 1**, correctly: exact expected `kenney_shape-characters/` does not exist. The genuine pack exists at **`kenney_shape-characters ` with one trailing ASCII space**. This is an explicitly inspected spelling discrepancy, not an absent pack or silent substitution. Folder was not renamed. Read its actual PNGs, XML and license through the space-bearing path. All 14 expected individual files exist; backgrounds/walls/sounds directories exist. Other sound candidates were enumerated but not assigned. Python/Pillow (already available), PNG headers, XML, wave and installed ffprobe used; nothing installed. Temporary resized/contact-sheet previews were used only for inspection under `/tmp/spec13-*`; no production-derived assets were created.

## B2. Exact approved inventory

All paths in this report are relative to the verified asset root unless specified.

| Source | Presence / bytes / notes |
|---|---|
| `characters.md` | present, 5321; source of anatomy/expressions, latest 0/1 correction in skill applies |
| `backgrounds/snow_arena_master.png` | present, 1,410,328 |
| `backgrounds/snow_arena.webp` | present, 757,356 |
| `kenney_shape-characters ` | actual trailing-space directory, original PNG/Default + PNG/Double, Spritesheet PNG/XML, Vector SVG/SWF, Sample and License |
| `kenney_pixel-platformer-blocks/` | present; 324 individual tiles, eight tilemaps, Tilesheet.txt/License.txt and previews |
| `haunted_candle.png` | present, 9,651,115 |
| `smoke_transition.png` | present, 20,267,858 |
| Nine literal `sounds/` paths | all present, exact case; full metadata B7 |

No JSON/XML/sidecar animation manifest for the candle/smoke sheets was found. Relevant metadata files across source root are Kenney licenses/Tilesheet.txt and character atlas XMLs; no audio license document found there. Neither font family is supplied in this source root/app.

## B3. Snow master and runtime

| Property | PNG master | Runtime WebP |
|---|---|---|
| Dimensions | 1672 × 941 | 1672 × 941 |
| Aspect | 1672/941 = **1.776833** | same; approximately 0.053% narrower than 16:9 |
| Mode / alpha | RGB, PNG type 2, no transparency | RGB, no transparency |
| Bytes | 1,410,328 | 757,356 |

Both decoded images were compared with Pillow `ImageChops.difference`: **no differing decoded RGB pixels** (`getbbox() is None`). Binary hashes were not required. Visual inspection confirms pale snowy ground and the large horned monster shadow with extended clawed arms in the lower image; no prepainted maze/wall/player sprites. Preserve the PNG master. Use WebP unchanged at runtime with aspect-preserving contain fit; the tiny ratio mismatch can leave a subpixel/slender margin within a complete 1600×900 world rather than distorting/cropping either world or image. Snow is atmosphere, never collision geometry.

## B4. Character sources, atlas and expression confidence

Authority: actual `characters.md` and skill ref 04. Four upright, head-only silhouettes, no limbs or head rotation; pupils alone indicate gaze. **There is no numeric head/face display scale or assembly anchor specification in `characters.md`.** Part sizes below are source pixels, not world collider dimensions. Opus must retain facial relationships through real-scale review; simulation collider is 48×48 for all four.

Let **C = `kenney_shape-characters `** (including trailing space). Individual transparent PNGs exist; extraction from a screenshot or sheet is unnecessary for most pieces. Default atlas `C/Spritesheet/spritesheet_default.png` **577×577**; double **1154×1154**, each with matching XML. Both PNG/Default and PNG/Double provide individual files, double dimensions twice default. **Double atlas packing differs; do not simply double default XY coordinates.** XML is the crop authority, top-left `(x,y,width,height)`.

| Approved identity | Actual Default source / atlas bounds | Confidence |
|---|---|---|
| P1 orange circle | **No `orange_body_circle.png`** or orange-named body found. `yellow_body_circle.png` `(0,387,80,80)` is a genuine warm yellow-to-orange gradient circle, visually inspected | Candidate only: confirm whether this is the intended orange P1 source or obtain approved orange derivative/source mapping; no recolor performed |
| P2 pink square | `PNG/Default/pink_body_square.png`; `(320,80,80,80)` | exact shape/color source present |
| P3 yellow diamond | `PNG/Default/yellow_body_rhombus.png`; `(80,227,80,80)` | exact source present; source calls diamond rhombus |
| P4 green rounded square | `PNG/Default/green_body_squircle.png`; `(160,480,80,80)` | exact source present |

Shapes are 80×80 Default / 160×160 Double. Pink square RGBA; other listed shapes palette mode P with **tRNS alpha**, decoded alpha 0–255. Do not discard palette transparency. `facial_part_eye_open.png` and `face_j.png` likewise P+tRNS alpha 0–255. Some narrow brow images have minimum alpha 63 rather than a fully transparent pixel; do not infer an opaque background from image mode or force a new black/white matte.

Verified reusable **Default** parts (`PNG/Default/`, atlas bounds from `spritesheet_default.xml`):

| Filename suffix (all begin `facial_part_`) | x,y,w,h |
|---|---|
| `eye_open.png` | 480,211,20,20 |
| `eye_closed_down.png` | 224,564,23,11 |
| `eye_closed_up.png` | 247,564,23,11 |
| `eye_half_bottom.png` | 290,567,20,10 |
| `eye_half_top.png` | 270,564,20,10 |
| `eye_half_top_wing.png` | 199,564,25,10 |
| `eyebrow_a.png` | 59,571,20,5 |
| `eyebrow_b.png` | 350,564,20,7 |
| `eyebrow_c.png` | 310,572,15,5 |
| `eyebrow_d.png` | 310,564,20,8 |
| `mouth_happy.png` | 330,564,20,7 |
| `mouth_sad.png` | 290,560,20,7 |
| `mouth_smirk.png` | 144,122,14,7 |
| `mouth_angry.png` | 159,564,40,10 |

Standalone `face_a.png`–`face_l.png` also exist. Exact default atlas crops: a `(109,547,50,29)`, b `(59,547,50,24)`, c `(0,547,59,30)`, d `(400,531,53,33)`, e `(400,350,55,40)`, f `(400,422,55,33)`, g `(400,390,55,32)`, h `(400,455,55,36)`, i `(96,80,59,42)`, j `(416,37,50,39)`, k `(400,491,55,40)`, l `(416,0,53,37)`. Visual inspection: a open-eye smile; b half-eye frown; c cheerful arced eyes; d asymmetrical gaze/smirk; e/f angry brows; g angry/white clenched mouth; h half-eye smirk; i worried open-eye frown; **j X-eyes with sad mouth**; k closed-eye frown; l closed-eye smile. These descriptions identify source content, **not approved final expression assignment**.

| Required expression | Source-backed mapping / exact unresolved piece |
|---|---|
| Default / charge 2 | open eye, relaxed brow parts present; **no named flat neutral mouth**. Default emotional meaning is plain/neutral, not face_a's smile |
| Charge 0 | same as charge 1; `mouth_sad`, open/half-eye and brow parts available; exact composition/anchors not supplied |
| Charge 1 | same frown/no halo as 0; `face_i` is a worried source candidate but not approved as the exact composed face |
| Charge 3 | `mouth_happy` and open eyes; final calm-smile assembly still Opus-owned |
| Charge 4 | only one named `mouth_happy` part; **distinct broad grin/lifted-brow recipe not mapped**. Don't label face_a as approved stage 4 without review |
| Manual full-power release | closed crease eye parts present; **no standalone dark open oval mouth** / complete scream face identified |
| Burst-hit override | X eyes genuinely present in `face_j`; **no standalone X-eye crop metadata or prescribed crooked pained mouth**. Whole face_j is a frown, not proof of exact authored reaction |
| Lower-power contact | `face_i` worried candidate; **small-pupil fear and alarmed oval mouth recipe not supplied** |
| Winner | joyful curved-eye parts / face_c or face_l candidate; **triumphant broad-mouth recipe not established** |
| Permanent evil ghost | half-eye, brow, smirk parts and angry face_e/f/h candidates exist; final exact assembly remains unmapped |
| Directional pupils | dark pupils are **baked into open/half-eye PNGs**; no standalone pupil/white-eye assets found. Moving the full eye is not pupil-only gaze; exact source separation/crop/mask recipe unresolved |

**BLOCKER for full 13.3 acceptance:** searched the genuine C/PNG/Default, PNG/Double, Spritesheet XML/PNGs and inspected their content, not marketplace previews. Smallest follow-up: asset owner/Opus confirm P1 warm-circle mapping and specify approved derived assembly/masks or supply missing neutral/oval/crooked/grin/pupil parts. Existing shape/face material is real; no need to search for another character pack. No internal face sub-crop coordinates invented; final compositions must match the authored vocabulary and 0/1 correction. Source art is not modified here.

## B5. Walls and geometry alignment

`kenney_pixel-platformer-blocks/Tilesheet.txt` explicitly gives **18×18**, 1px spacing, **9×9 =81** per material. Measured **324** individual PNGs, all **18×18**: `Tiles/{Marble,Rock,Stone,Sand}/tile_0000.png` through `tile_0080.png`. Modes P+tRNS and RGBA; sampled Stone alpha 0–255. Atlas `Tilemap/{marble,rock,stone,sand}.png` **170×170**; corresponding `_packed.png` **162×162**. Spaced atlas crop formula is `(19*col,19*row,18,18)`, packed `(18*col,18*row,18,18)`; individual PNGs avoid crop ambiguity.

Contact-sheet inspection of original tiles confirms bevels, inset blocks, slope pieces, corner and straight-edge patterns. Exact candidate family **`Tiles/Marble/tile_0027.png`–`tile_0035.png`** contains top-left/top/top-right and associated inset/bevel treatments; `tile_0036.png`–`tile_0044.png` includes center/side/lower edge variants. These are visual candidates, not a finalized wall assembly. Rock/Stone offer alternate material versions of the modular vocabulary. Grey/slate derivative and exact end-cap/corner combination belong to Opus 13.2; retain shading/alpha, don't flatten the material or ship brown by accident.

**18px is source texture resolution.** No tile defines a world cell. Use the nine actual collider rectangles in contract map A2; boundary placement and all visible segment extents must align with them. Source geometry is known, production browser geometry/plan access remains a B1 blocker. No maze inferred from the artwork; no wall atlas generated now.

## B6. Candle and smoke — measured versus unresolved

| Property | `haunted_candle.png` | `smoke_transition.png` |
|---|---|---|
| Dimensions | **4130×3501** | **7540×4806** |
| Size | 9,651,115 bytes | 20,267,858 bytes |
| Mode / alpha | RGBA, alpha extrema 0–255 | RGBA, alpha extrema 0–255 |
| Embedded/sidecar metadata | Pillow info empty; no source animation manifest found | same |
| Visual organization | ten visible equal-width columns × nine rows of orange flame/glow images | regular tiled smoke expansion/contraction, many transparent leading/trailing regions; source includes flat opaque interior coverage during middle frames |
| Safe frame conclusion | **90 visible cells**; equal-cell hypothesis 413×389, **not certified exporter frame rectangles/order** | **84 frames NOT confirmed**; do not count nonempty crops as frames or discard intentional empty frames |
| Timing | 30FPS /3s only historical skill description | 30FPS /2.8s only historical skill description |

No animation order/FPS/padding metadata exists beside these files. Exact export frame boundaries and chronological direction remain **VERIFY**. A diagnostic 10×9 smoke subdivision (754×534) found alpha-empty cells in row 0 columns 0–5, row 7 columns 6–9 and entire row 8; this is **not** an approved slice grid or frame count. Different source packing/empty frames cannot be resolved by divisibility alone. Candle equal subdivisions are all nonempty; global transparent row bands include 0–4, 778–782, 2723–2727, 3112–3116, with no fully transparent global column. Thus an automatic trim/bounding-box algorithm is unsafe.

Visual inspection confirms transparent exterior regions rather than a uniform opaque full-sheet background. Candle cells already contain a substantial warm circular glow; don't mistake it for a separate rectangle or add light assuming source is bare flame. Smoke includes intentionally solid transition-covering areas; potential seams, borders/bleed and first/middle/last **isolated-frame** transparency are not certified until source frame mapping is established. No arbitrary single-row strip, row-major timing or 256×384 assumption is accepted.

**BLOCKER for animated 13.5/13.6 acceptance:** asset owner supplies export metadata (frame rectangles, sequence including blank frames, FPS) or implementation owner establishes and visually validates exact frames against original export. Minimum confirmed useful metadata is actual sheet size/mode/alpha, visible layout and explicit uncertainty above. No frame atlas produced now.

GPU risk: widths 4130 and 7540 both exceed 4096; actual graphics-context `MAX_TEXTURE_SIZE` **not queried**. Native decoded RGBA storage alone is ~57.8MB /145.0MB decimal (~55.2/138.2MiB), before GPU duplication/mipmaps. Derived smaller/shared multi-atlases or downsampled frames will likely be needed after frame mapping and target GPU measurement. Preserve source files and avoid rendering native huge sheets blindly.

## B7. Nine approved audio metadata entries

Exact names below are all under **`sounds/`**, present/readable via wave/ffprobe. Hz and channels are measured; durations rounded to 3 decimals. This is metadata validation, not browser decode/listening/loop acceptance.

| Exact filename | Bytes | Codec | Seconds | Hz / channels | Available provenance |
|---|---:|---|---:|---|---|
| `ghost_main_theme.wav` | 13,419,736 | PCM s24le WAV | 50.714 | 44100 /2 | REAPER tag, date 2021-07-15; no creator/license tag |
| `multiplayer_theme.wav` | 5,647,488 | PCM s16le WAV | 32.015 | 44100 /2 | no identifying creator/license tag |
| `gameLoading.wav` | 14,817,644 | PCM s24le WAV | 56.000 | 44100 /2 | no identifying creator/license tag |
| `candle_eat.wav` | 39,522 | PCM s24le WAV | 0.250 | 48000 /1 | artist Chris Lucas “oGnomo”, REAPER/date tags; no license |
| `player_hit.wav` | 147,820 | PCM s24le WAV | 1.000 | 48000 /1 | artist oGnomo; comment describes lo-fi female character hurt/exertion; no license |
| `blast.wav` | 369,328 | PCM s16le WAV | 2.070 | 44100 /2 | no identifying creator/license tag |
| `menu.wav` | 39,522 | PCM s24le WAV | 0.250 | 48000 /1 | artist Chris Lucas “oGnomo”, REAPER/date tags; no license |
| `Gushy_wind.mp3` | 287,232 | MP3 | 8.976 | 48000 /2 | no creator/license tag; another wind candidate's filename is not proof of this renamed file's origin |
| `Ghost_knocking.wav` | 1,116,600 | PCM s24le WAV | 3.865 | 48000 /2 | artist Chris Lucas “oGnomo”, REAPER/date tags; no license |

No metadata decoding errors in this set. Tags are attribution evidence only, not redistribution grants. Do not silently replace any file (including approved player_hit) based on description or identical metadata/size to another cue. Local browser codec/autoplay/gain/loop edges remain untested. Exact approved behavior retained: day MP/original uses multiplayer_theme; active night uses ghost_main_theme after ready/reveal; other contexts gameLoading. Night ambience sequences **two complete Gushy_wind plays then one complete Ghost_knocking**, repeating, completion-driven; no sequence implementation here.

## B8. Provenance and rights evidence

| Asset family | Available evidence | Remaining requirement |
|---|---|---|
| Shape Characters 1.0 | actual C/License.txt: Kenney, dated 26/10/2023, CC0, personal/educational/commercial use | preserve source notice and derived mapping |
| Pixel Platformer Blocks 1.0 | actual License.txt: Kenney, dated 27-11-2021, CC0 | preserve notice |
| Snow | user-approved AI-generated source documented by installed skill; actual master/runtime inspected | no separate generator/export/license record in folder; retain user provenance, no invented grant |
| Candle/smoke | installed skill identifies ParticleFX Studio, historical 90/84-frame descriptions | no download-specific license/manifest in actual folder; owner provides source/download grant and raw-repository redistribution evidence |
| All nine audio | metadata listed above; no local license documents | owner provides per-file source/creator/license and game/raw-source distribution permission; no blanket CC0 assertion |
| Source Serif 4 / Source Sans 3 | selected by skill; no local runtime files/OFLs found | later implementation owner obtains approved font files and matching OFLs; no extra typeface substituted |

No web/license investigation or external terms inference performed. Unknown rights **block public distribution**, not unrelated local UI foundation. Asset accessibility and license clearance are separate claims.

## B9. Runtime packaging guidance — later owner work only

Use existing Vite application `web/apps/ghost-lap`, central registry and namespaced served output (proposed `public/game-assets/`, currently absent). Copy approved runtime WebP and cleared sounds later; preserve source filenames/case. Resolve the trailing-space source directory explicitly in build-time paths, not via hidden fuzzy matching. Do not expose local `/Users/` or `file://` paths to browser. Keep master PNG, unused hands/limbs/grass, pack screenshots, other audio candidates and archival downloads out of runtime.

Opus 13.2/13.3 owns approved derived grey wall/character assemblies; Opus effect slices own candle/smoke sequences after source metadata is verified. Derived atlases must have separate output/provenance, real frame rectangles, shared textures, alpha preserved and appropriate dimensions for measured target GPUs. B1 supplies actual geometry/plan reads and server identity, not pixel-derived gameplay. B4/B5 later verify served URLs/base paths, MIME, browser codecs/GPU and rights. No raw source copying, asset generation or font/package installation was done in 13.0.

## B10. Critical blockers / smallest follow-up

| Issue | Owner / smallest factual follow-up | Affected gate |
|---|---|---|
| Trailing-space character directory | implementation owner explicitly maps actual path in asset preparation; no rename required for audit | NONBLOCKING VERIFY 13.1; exact-path packaging check later |
| Orange P1 mapping and exact neutral/scream/fear/grin/pupil parts | asset owner + Opus confirm warm yellow_body_circle mapping and approved derived part recipes or provide missing source; use real XML, no screenshot reconstruction | **BLOCKER full 13.3** |
| Effect frame rectangles/order/FPS unknown | asset owner supplies exporter manifest or implementation owner validates exact sequence, including empty smoke frames | **BLOCKER 13.5/13.6 animation acceptance** |
| Native effect sizes/GPU limit | Codex B4 queries real context; implementation owner creates verified smaller atlases later | BLOCKER shipping oversized textures on unsupported GPU; no measured limit claimed |
| Geometry/plan access and original-live data gap | Codex B1, contract map A8 | **BLOCKER 13.2/13.4–13.5 integration** |
| Audio/FX redistribution grants absent | asset owner provides evidence, Codex B5 checks | **BLOCKER public release**; 13.1 can proceed |

Audit conclusion: source art/audio is genuinely accessible; approved direction remains intact. A reliable foundation can begin with these explicit boundaries. No complete character acting system, exact effect atlas, browser visual quality, FPS, deployment, test or green CI is claimed.
