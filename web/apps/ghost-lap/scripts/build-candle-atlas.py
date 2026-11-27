#!/usr/bin/env python3
"""Build the ONE candle flame atlas from design-inputs/game-assets/haunted_candle.png (ParticleFX orange candle; the master is only READ).

WHY a derived atlas (13.5): the master is 4130x3501 (about 58 MB decoded, wider than 4096), so it is never uploaded to the browser. It is also
an ADDITIVE-style effect sheet: every frame carries a wide baked glow whose RGB is a constant brown (about 123,79,34) with a varying alpha.
Normal-blended on pale snow that reads as a dirty smudge, and additive on a blue night it greys out. So the runtime separates the two:
  * FLAME layer (this atlas): the master frame with its constant-colour glow keyed out by luminance, so only the real flame, its orange
    rim and the spark curl remain. Frame ORDER, count and every pixel of the flame are the master's.
  * LIGHT: a shared soft radial pool built at runtime (src/lighting), warm and low in day, stronger at night.

EVIDENCE for the frame grid and order (computed here, not assumed; see the "evidence" block written to candleAtlas.json):
  * Occupied-area runs of the alpha channel give exactly 10 columns x 9 rows, pitch 413 x 389, each cell a 360x360 content square at
    (26 + 413*col, 17 + 389*row). No frame is skipped; all 90 are non-empty.
  * Consecutive frames in ROW-MAJOR order (left to right, top to bottom) are far closer to each other than random pairs, or than
    COLUMN-MAJOR neighbours, and frame 89 closes back to frame 0. Direction (forward = reading order) is the natural one: a one-frame flash
    onset followed by a slow decay. It is a derived judgement; the study screen lets a human judge it and plays the reverse on request.
  * No exporter manifest exists. 30 fps / 3 s is the skill's nominal description of this download, NOT export evidence.

Outputs (generated; safe to delete and rebuild):
  public/game-assets/generated/candle-flame.png   (10 x 9 cells of 224 x 224, same index as the master's row-major frame)
  src/config/candleAtlas.json                     (frame rectangles, anchor, loops, per-frame light gain, evidence, derivation)
Run from web/apps/ghost-lap:  python3 scripts/build-candle-atlas.py
"""
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None
APP = Path(__file__).resolve().parent.parent
SRC = APP.parents[2] / "design-inputs/game-assets/haunted_candle.png"

COLS, ROWS = 10, 9
PITCH_X, PITCH_Y = 413, 389
BOX_X, BOX_Y, BOX = 26, 17, 360          # content square inside each pitch cell, source pixels
CROP_CX, CROP_CY, CROP = 180, 168, 320   # window kept around the flame, content-square pixels (the flash frames reach about r 150)
ANCHOR_X, ANCHOR_Y = 180, 172            # flame heart in the content square: this point sits on the candle site
SCALE = 0.7                              # source px -> atlas px
CELL = int(CROP * SCALE)                 # 224
KEY_LO, KEY_HI = 0.40, 0.70              # smoothstep on straight luminance: glow (about 0.32) -> out, flame orange/white -> in
FLASH_FIRST, FLASH_LAST = 38, 61         # measured white-pink flash run (see main(): flame mass 1.55x -> 9.5x -> 1.2x of the median)


def smoothstep(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3 - 2 * t)


def content(sheet: Image.Image, i: int) -> np.ndarray:
    r, c = divmod(i, COLS)
    x, y = BOX_X + PITCH_X * c, BOX_Y + PITCH_Y * r
    return np.asarray(sheet.crop((x, y, x + BOX, y + BOX))).astype(np.float32)


def key_glow(a: np.ndarray) -> np.ndarray:
    lum = (a[..., 0] * 0.2126 + a[..., 1] * 0.7152 + a[..., 2] * 0.0722) / 255.0
    out = a.copy()
    out[..., 3] *= smoothstep((lum - KEY_LO) / (KEY_HI - KEY_LO))
    return out


def resize_premultiplied(a: np.ndarray, size: int) -> np.ndarray:
    p = a.copy()
    p[..., :3] *= p[..., 3:4] / 255.0
    im = Image.fromarray(np.clip(p, 0, 255).astype(np.uint8)).resize((size, size), Image.LANCZOS)
    q = np.asarray(im).astype(np.float32)
    alpha = q[..., 3:4]
    q[..., :3] = np.where(alpha > 0, q[..., :3] * 255.0 / np.maximum(alpha, 1.0), 0)
    return np.clip(q, 0, 255)


def main() -> None:
    sheet = Image.open(SRC).convert("RGBA")
    if sheet.size != (COLS * PITCH_X - 0 + 0, ROWS * PITCH_Y + 0) and sheet.size != (4130, 3501):
        raise SystemExit(f"unexpected master size {sheet.size}")
    master_sha = hashlib.sha256(SRC.read_bytes()).hexdigest()

    # ---- evidence: grid occupancy ----------------------------------------------------------------------------------------
    alpha = np.asarray(sheet)[..., 3] > 8
    def runs(v):
        out, s = [], None
        for i, x in enumerate(v):
            if x and s is None: s = i
            if not x and s is not None: out.append((s, i - 1)); s = None
        if s is not None: out.append((s, len(v) - 1))
        return out
    col_runs, row_runs = runs(alpha.any(axis=0)), runs(alpha.any(axis=1))
    assert len(col_runs) == COLS and len(row_runs) == ROWS, (col_runs, row_runs)
    assert all(b - a + 1 == BOX for a, b in col_runs + row_runs), "content squares are not all 360 px"
    assert [a for a, _ in col_runs] == [BOX_X + PITCH_X * c for c in range(COLS)]
    assert [a for a, _ in row_runs] == [BOX_Y + PITCH_Y * r for r in range(ROWS)]

    frames = [content(sheet, i) for i in range(COLS * ROWS)]

    # ---- evidence: order continuity (premultiplied 90x90 thumbnails) -----------------------------------------------------
    thumbs = []
    for a in frames:
        t = np.asarray(Image.fromarray(a.astype(np.uint8)).resize((90, 90), Image.BOX)).astype(np.float32) / 255.0
        t[..., :3] *= t[..., 3:4]
        thumbs.append(t.reshape(-1))
    t = np.array(thumbs)
    d = np.sqrt(((t[:, None, :] - t[None, :, :]) ** 2).sum(-1))
    n = len(frames)
    step = lambda order: [float(d[order[i], order[(i + 1) % n]]) for i in range(n)]
    row_major = list(range(n))
    col_major = [r * COLS + c for c in range(COLS) for r in range(ROWS)]
    rm, cm = step(row_major), step(col_major)
    nn = np.argsort(d + np.eye(n) * 1e9, axis=1)[:, :2]
    successor_near = sum(1 for i in range(n - 1) if (i + 1) in nn[i])

    # ---- derived flame frames -----------------------------------------------------------------------------------------------
    x0, y0 = CROP_CX - CROP // 2, CROP_CY - CROP // 2
    atlas = np.zeros((ROWS * CELL, COLS * CELL, 4), np.uint8)
    yy, xx = np.mgrid[0:CROP, 0:CROP]
    radius = np.hypot(xx - (CROP - 1) / 2, yy - (CROP - 1) / 2) / (CROP / 2)
    edge_fade = 1.0 - smoothstep((radius - 0.80) / 0.20)
    mass = []
    border = 0
    for i, a in enumerate(frames):
        keyed = key_glow(a)[y0:y0 + CROP, x0:x0 + CROP].copy()
        # The flash frames carry a faint pink halo tail out to the window edge. A circular fade over the outer 20 percent of the window
        # keeps the cell edge exactly transparent (no square edge on snow, no bleed between neighbouring cells under mip-mapping).
        keyed[..., 3] *= edge_fade
        lum = (keyed[..., 0] * 0.2126 + keyed[..., 1] * 0.7152 + keyed[..., 2] * 0.0722) / 255.0
        mass.append(float((lum * keyed[..., 3] / 255.0).sum()))
        small = resize_premultiplied(keyed, CELL)
        border = max(border, small[0].max(initial=0) if False else int(max(small[0, :, 3].max(), small[-1, :, 3].max(), small[:, 0, 3].max(), small[:, -1, 3].max())))
        r, c = divmod(i, COLS)
        atlas[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = small.astype(np.uint8)
    assert border <= 3, f"flame content touches the crop window (border alpha {border}); widen CROP"

    median = float(np.median(mass))
    gain = [round(float(np.clip(m / median, 0.35, 2.2)), 3) for m in mass]
    # Measured flame mass / median: ordinary flicker 0..37 and 62..89 stays inside 0.15..1.55; frames 38..61 rise to 9.5x (one-frame onset at
    # 38/39, slow decay) = the white-pink flash. The run is fixed here from that measurement and the assertions keep it honest.
    flash = list(range(FLASH_FIRST, FLASH_LAST + 1))
    outside = [m / median for i, m in enumerate(mass) if i not in set(flash)]
    assert max(outside) <= 1.6 and max(m / median for i, m in enumerate(mass) if i in set(flash)) >= 5, "flash run no longer matches the data"
    steady = [i for i in range(n) if i not in set(flash)]
    join = float(d[flash[0] - 1, flash[-1] + 1])

    out_png = APP / "public/game-assets/generated/candle-flame.png"
    out_png.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(atlas, "RGBA").save(out_png, optimize=True)

    frames_meta = {str(i): {"x": (i % COLS) * CELL, "y": (i // COLS) * CELL, "w": CELL, "h": CELL} for i in range(n)}
    meta = {
        "meta": {
            "source": "design-inputs/game-assets/haunted_candle.png",
            "sourceSha256": master_sha,
            "sourceSize": [sheet.width, sheet.height],
            "sourceUntouched": True,
            "provenanceNote": "ParticleFX Studio orange candle per the installed skill; no download licence or exporter manifest is in the folder (asset audit B8). Redistribution evidence remains an owner item.",
            "cell": CELL,
            "columns": COLS,
            "rows": ROWS,
            "frameCount": n,
            "fps": 30,
            "fpsNote": "NOMINAL 30 fps / 3 s from the skill description of the download. Not export evidence.",
            "anchor": [round((ANCHOR_X - x0) * SCALE, 1), round((ANCHOR_Y - y0) * SCALE, 1)],
            "sourcePixelsPerWorldUnit": round(1 / 0.36, 3),
            "derivation": {
                "grid": {"pitch": [PITCH_X, PITCH_Y], "contentBox": [BOX_X, BOX_Y, BOX, BOX], "order": "row-major, frame index = row*10 + col"},
                "crop": {"centre": [CROP_CX, CROP_CY], "size": CROP, "atlasScale": SCALE},
                "glowKey": {"op": "alpha *= smoothstep(lum(R,G,B straight))", "lo": KEY_LO, "hi": KEY_HI, "why": "baked glow is constant brown RGB(123,79,34) at varying alpha; additive-style, brown on snow"},
                "edgeFade": "alpha *= 1 - smoothstep((r/R - 0.80)/0.20) over the circular crop window, so the cell edge is exactly transparent",
                "resize": "Lanczos on premultiplied colour, so no dark fringe",
            },
            "evidence": {
                "columnRuns": col_runs,
                "rowRuns": row_runs,
                "rowMajorMeanStep": round(float(np.mean(rm[:-1])), 3),
                "rowMajorMaxStep": round(float(np.max(rm[:-1])), 3),
                "rowMajorMaxStepAt": int(np.argmax(rm[:-1])),
                "rowMajorLoopClose": round(rm[-1], 3),
                "columnMajorMeanStep": round(float(np.mean(cm[:-1])), 3),
                "randomPairMean": round(float(d[np.triu_indices(n, 1)].mean()), 3),
                "successorAmongTwoNearest": successor_near,
                "of": n - 1,
            },
            "loops": {
                "source": {"frames": list(range(n)), "note": "All 90 frames in reading order. Includes the white-pink flash (frames %d to %d)." % (flash[0], flash[-1])},
                "steady": {"frames": steady, "note": "The same frames without the flash run; the join is a normal flicker step.", "joinStep": round(join, 3)},
            },
            "flashFrames": [flash[0], flash[-1]],
            "lightGain": gain,
            "lightGainNote": "Flame luminous mass per frame / median, clamped 0.35..2.2. Optional gentle coupling of the warm pool to the same frame.",
        },
        "frames": frames_meta,
    }
    (APP / "src/config/candleAtlas.json").write_text(json.dumps(meta, indent=1))
    print("atlas", atlas.shape[1], "x", atlas.shape[0], out_png.stat().st_size // 1024, "KiB")
    print("row-major mean step", meta["meta"]["evidence"]["rowMajorMeanStep"], "column-major", meta["meta"]["evidence"]["columnMajorMeanStep"],
          "random", meta["meta"]["evidence"]["randomPairMean"], "loop close", meta["meta"]["evidence"]["rowMajorLoopClose"],
          "successor near", successor_near, "/", n - 1)
    print("flash", flash[0], flash[-1], "steady join step", round(join, 3), "border alpha", border)


if __name__ == "__main__":
    main()
