# 01 — Asset manifest, exact location, preparation and legal checks

## Absolute source root

`/Users/rroy/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/`

Set `GAME_ASSETS="$HOME/Desktop/the-sands-of-time-phase1/design-inputs/game-assets"`. Actual Mac path was shown in Finder screenshots. This directory is a **design-input source**; the frontend repo and its deployable `public/` location are not yet verified. Do **not** use `file:///Users/...` runtime URLs. Use a build-safe served path after the backend/main agent locates the frontend app. `scripts/asset_inventory.py` reports visible local files without altering them.

## Exact current source tree — user-confirmed expected names

```text
/Users/rroy/Desktop/the-sands-of-time-phase1/design-inputs/game-assets/
├── characters.md                       # AUTHORITATIVE expression/design specification
├── backgrounds/
│   ├── snow_arena_master.png             # source master, preserve as-is
│   └── snow_arena.webp                   # intended runtime background, lossless source copy
├── kenney_pixel-platformer-blocks/      # Kenney wall/block source pack, original names retained
├── kenney_shape-characters/             # Kenney head/face source pack, original names retained
├── haunted_candle.png                   # ParticleFX Studio orange candle sprite sheet
├── smoke_transition.png                 # ParticleFX Studio smoke transition sheet
└── sounds/
    ├── ghost_main_theme.wav
    ├── multiplayer_theme.wav
    ├── gameLoading.wav
    ├── candle_eat.wav                     # spelling EXACT: candle, not candel
    ├── player_hit.wav
    ├── blast.wav
    ├── menu.wav
    ├── Gushy_wind.mp3                    # MP3: capital G and underscore
    ├── Ghost_knocking.wav               # WAV: capital G and underscore
    └── [other WAV/MP3 candidates not approved]
```

**CONFIRMED:** user has placed these assets at the described locations. **VERIFY:** the coding environment must enumerate and inspect those exact files. This skill package intentionally does **not** contain or redistribute their raw graphics/audio.

## Approved source-to-runtime mapping

| Source | Purpose | Conversion allowed | Restrictions |
|---|---|---|---|
| `backgrounds/snow_arena_master.png` | high-quality original art; pale snowy ground with giant monster silhouette/shadow | keep intact as master | never use screenshot with drawn walls/characters |
| `backgrounds/snow_arena.webp` | runtime terrain background | preload; use without re-encoding unless measured need | maintain aspect ratio, soft shadow, no green replacement |
| `kenney_pixel-platformer-blocks/` | modular wall/block top/side/corner/end variants | derived *grey* textures/atlas; preserve original pack | 18×18 pixel tile scale is texture resolution, **not** collision/world size |
| `kenney_shape-characters/` | colour-gradient body shapes and face elements | compose/crop from original **transparent** source assets | characters.md selection; no arm/hand/leg assets |
| `haunted_candle.png` | orange-flame candle animation | slice/pack/downsample as performance requires | maintain chosen **orange** variant; day/night same frames |
| `smoke_transition.png` | scene-changing smoke/fog | slice/pack/downsample as performance requires | only on transition; not perpetual atmospheric blanket |
| nine approved audio files | menu/BGM/cues/ambient | lossless or sensible web-safe transcode only after comparing playback | exact names are source truth; do not rename or assign undeclared files |

**Previously discarded:** grass arena images, green environment, arbitrary AI render mockups (including screenshots with prepainted walls/characters). Do not deploy them.

## Compile/serve policy

1. Keep source master PNG and original downloaded packs in `design-inputs/game-assets/` as immutable source inputs.
2. In existing frontend app, use its verified static assets mechanism (Vite commonly `public/game-assets/`, Next commonly `public/game-assets/`; confirm bundler). Copy approved **runtime** files into a namespaced serving location without changing filenames. Use central `AssetRegistry` to resolve URLs.
3. **Exclude `snow_arena_master.png`** from public runtime copy if identical WebP is served. Keep masters in source inputs for future adjustments.
4. Create **generated** atlases and metadata in a separate output (e.g. `public/game-assets/generated/`), never overwrite upstream Kenney or ParticleFX files. Keep provenance/README.
5. Ensure URLs work under the project's actual base path and production build; no `/Users/...` runtime references. Build tooling may use local path **only while copying**.
6. The agent may rename derived atlas entries (e.g. `orange/circle.png`) but not the approved source filenames. Any new aliases live in the registry, not in ad-hoc import strings.
7. Do not ship screenshots and visual references as scene assets.

## Asset inspection procedure (required)

- `ls -la "$GAME_ASSETS"; find "$GAME_ASSETS" -maxdepth 3 -type f | sort` — verify exact capitalization and structures.
- Inspect each Kenney folder for actual PNGs, spritesheets, atlas JSON/XML, tile names, sprite bounds and alpha. Do **not** attempt sprite cropping based on blue background marketplace screenshots.
- Test actual character shape assembly at gameplay display size. If face elements are present as separate PNGs, use them; if packed, generate metadata/crop **after examining original files**. Do not invent atlas coordinates.
- Inspect `haunted_candle.png` and `smoke_transition.png` for actual sheet width/height, row/column count, frame dimensions, alpha and order. Published descriptions indicate **90 frames/30 fps/3 seconds** (candle), **84 frames/30 fps/2.8 seconds** (smoke), but treat these as **VERIFY AGAINST DOWNLOADED FILES**. Sheet metadata/packing may differ; do not split from guessed grid.
- Very wide PNG sheets can exceed `MAX_TEXTURE_SIZE`. If so, slice into safe separate atlases and generate a PixiJS-readable JSON sequence. Check `gl.MAX_TEXTURE_SIZE` in actual environment. Avoid decoding every source frame at native 256×384 if the candle renders tiny. Keep an archival copy.
- Test first/mid/last animation frames on a transparency checkerboard and ensure no black/blue rectangle appears in-game.
- Inspect `.wav` sample rate/channels/duration and `Gushy_wind.mp3` duration (~9 s **nominal**); loop using completion/ended events, not a 9-second timeout. Read actual audio metadata, don't reassign other candidate sounds.
- Assert missing approved file is reported to operator; avoid silently falling back to random placeholders in release.

## Licensing/repository guardrails

- Kenney source packs are described as **CC0** by publisher; keep source/license acknowledgement metadata for provenance.
- ParticleFX Studio effects: source pages describe free use in games; **VERIFY download-specific license and conditions for publicly redistributing raw sheets in the project's repository**. Don't assume 'free commercial game use' equals 'can publish raw sheets standalone'.
- The user's AI-generated snowy background is the chosen game source asset; preserve original and user provenance.
- The **actual license of each downloaded audio file** is unknown in this context. Agent must obtain creator/link/license from user or metadata before making public-release assumptions. Do not invent CC0 for WAVs.
- Source visual references to Alto's Adventure and the banking UI remain inspiration only. Do **not** extract/ship their logos, menu textures, font files or other copyrighted media.

## Central asset registry — render-side semantics (not implementation of backend)

```ts
// EXAMPLE registry semantics; resolve served paths to this project's actual base URL.
const approvedAssetKeys = {
  snowArena: 'backgrounds/snow_arena.webp',
  candleSheet: 'haunted_candle.png',
  smokeSheet: 'smoke_transition.png',
  playerPack: 'kenney_shape-characters/',
  wallPack: 'kenney_pixel-platformer-blocks/',
  menuTheme: 'sounds/gameLoading.wav',
  dayTheme: 'sounds/multiplayer_theme.wav',
  nightTheme: 'sounds/ghost_main_theme.wav',
  eat: 'sounds/candle_eat.wav',
  hit: 'sounds/player_hit.wav',
  burst: 'sounds/blast.wav',
  menuClick: 'sounds/menu.wav',
  wind: 'sounds/Gushy_wind.mp3',
  knock: 'sounds/Ghost_knocking.wav',
} as const;
```

A registration entry is NOT evidence of successful file copying. Build and dev must test every served URL.
