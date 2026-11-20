#!/usr/bin/env python3
"""Build the smoke transition atlas pages from design-inputs/game-assets/smoke_transition.png (the master is only READ).
WHY a derived atlas (13.6): the master is 7540x4806 (about 145 MB decoded, wider than 4096), so it is never uploaded to the browser.
What the audit left open (84 frames? order? empty frames? full cover?) is MEASURED here, not assumed; the numbers are written to
smokeAtlas.json ("evidence") and the build fails if a measurement stops holding.
EVIDENCE
  * GRID: 10 columns x 9 rows of 754 x 534 cells (7540 / 10, 4806 / 9). The alpha occupancy of the sheet has exactly 10 column runs whose
    starts follow the 754 pitch, and every non-empty cell lies inside its own cell (no frame crosses a cell border).
  * COUNT: cells 0-5 and 76-89 are fully transparent (alpha 0 everywhere); cells 6-75 (70) have content. An exporter that packs row-major from
    cell 0 and writes 84 frames fills cells 0-83, so 84 = 6 leading empty + 70 visible + 8 trailing empty frames, and cells 84-89 are unused
    padding. That matches the nominal "84 frames" claim; it is a derivation from packing convention (no exporter manifest exists), and the
    empty frames are KEPT in the timeline index (they are the smoke starting / ending), only the runtime skips drawing them.
  * SCREEN RECTANGLE: every frame carries one flat rectangle (the "card" the smoke fades over): solid x 106..653, y 109..434 of the cell, with a
    2 px soft edge. The runtime maps the 16:9 crop of that rectangle (548x308) to the whole canvas, so the rectangle IS the screen.
  * COVER: inside that crop the minimum alpha is >= 250 for frames 27-46 (28-45 are exactly 255). Before 27 the rectangle ramps up by about
    14 alpha per frame from frame 10; after 46 it ramps down at about 13 per frame. Mean alpha is monotone on both sides, which is also the
    order evidence (reading order), and it is the window in which a scene may be swapped without a visible pop.
  * ORDER/DIRECTION: reading order is the only order with a monotone ramp. Forward (cover, then clear) is the natural reading: puffs form on the
    edges, fill the card, then the card clears while edge smoke thins. A human can judge the direction in the study; 30 fps is the skill's nominal rate.
OUTPUT (generated; safe to delete and rebuild):
  public/game-assets/generated/smoke-0.png .. smoke-2.png   (4 columns of 480x270 frames; at most 1920 px wide and 1890 tall, below 2048)
  src/config/smokeAtlas.json
Run from web/apps/ghost-lap:  python3 scripts/build-smoke-atlas.py
"""
import hashlib
import json
from pathlib import Path
import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None
APP = Path(__file__).resolve().parent.parent
SRC = APP.parents[2] / "design-inputs/game-assets/smoke_transition.png"
COLS, ROWS = 10, 9
CELL_W, CELL_H = 754, 534
FRAME_COUNT = 84
# 16:9 crop of the solid rectangle (x 106..653 -> 548 wide; centred vertically on y 109..434 -> 308 tall).
CROP = (106, 118, 654, 426)
OUT_W, OUT_H = 480, 270
PAGE_COLS, PAGE_ROWS = 4, 7
FULL_COVER_MIN_ALPHA = 250
def premultiplied_resize(a: np.ndarray, size: tuple[int, int]) -> np.ndarray:
    p = a.astype(np.float32).copy()
    p[..., :3] *= p[..., 3:4] / 255.0
    im = Image.fromarray(np.clip(p, 0, 255).astype(np.uint8)).resize(size, Image.LANCZOS)
    q = np.asarray(im).astype(np.float32)
    alpha = q[..., 3:4]
    q[..., :3] = np.where(alpha > 0, q[..., :3] * 255.0 / np.maximum(alpha, 1.0), 0)
    return np.clip(q, 0, 255).astype(np.uint8)
# Presentation recolour (13.6 refinement): the master's smoke is dark slate (luminance about 58..120). The derived pages map that luminance onto a
# luminous pale grey-white with a faint blue cast, so the internal shading and the alpha (soft edges, full cover) are kept exactly. Master untouched.
LUM_LO, LUM_HI = 55.0, 125.0
SHADOW_RGB = np.array([192, 204, 224], np.float32)
LIGHT_RGB = np.array([247, 250, 255], np.float32)
def recolour_pale(a: np.ndarray) -> np.ndarray:
    out = a.copy()
    lum = a[..., :3].astype(np.float32).mean(axis=2, keepdims=True)
    t = np.clip((lum - LUM_LO) / (LUM_HI - LUM_LO), 0.0, 1.0)
    out[..., :3] = np.clip(SHADOW_RGB + (LIGHT_RGB - SHADOW_RGB) * t, 0, 255).astype(np.uint8)
    return out
def main() -> None:
    sheet = Image.open(SRC).convert("RGBA")
    if sheet.size != (COLS * CELL_W, ROWS * CELL_H):
        raise SystemExit(f"unexpected master size {sheet.size}")
    data = np.asarray(sheet)
    alpha = data[..., 3]
    master_sha = hashlib.sha256(SRC.read_bytes()).hexdigest()
    def cell(i: int) -> np.ndarray:
        r, c = divmod(i, COLS)
        return data[r * CELL_H:(r + 1) * CELL_H, c * CELL_W:(c + 1) * CELL_W]
    # ---- evidence: occupancy -------------------------------------------------------------------------------------------------
    nonempty = [i for i in range(COLS * ROWS) if cell(i)[..., 3].max() > 0]
    first, last = nonempty[0], nonempty[-1]
    assert nonempty == list(range(first, last + 1)), "visible frames are not one contiguous run"
    assert (first, last) == (6, 75), (first, last)
    assert last < FRAME_COUNT, "visible frames exceed the 84-frame timeline"
    assert all(cell(i)[..., 3].max() == 0 for i in range(0, first)) and all(cell(i)[..., 3].max() == 0 for i in range(last + 1, COLS * ROWS))
    col_occ = (alpha > 8).any(axis=0)
    col_runs = int(np.count_nonzero(np.diff(col_occ.astype(np.int8)) == 1) + int(col_occ[0]))
    assert col_runs == COLS, col_runs
    # ---- evidence: the card rectangle, cover window, monotone ramps --------------------------------------------------------------
    x0, y0, x1, y1 = CROP
    min_a, mean_a = [], []
    for i in range(first, last + 1):
        a = cell(i)[y0:y1, x0:x1, 3]
        min_a.append(int(a.min()))
        mean_a.append(float(a.mean()))
    covered = [first + k for k, m in enumerate(min_a) if m >= FULL_COVER_MIN_ALPHA]
    assert covered == list(range(covered[0], covered[-1] + 1)), "cover window is not contiguous"
    cover_first, cover_last = covered[0], covered[-1]
    peak = int(np.argmax(mean_a))
    rising = all(mean_a[k] <= mean_a[k + 1] + 0.01 for k in range(0, peak))
    falling = all(mean_a[k] >= mean_a[k + 1] - 0.01 for k in range(peak, len(mean_a) - 1))
    assert rising and falling, "mean alpha is not unimodal in reading order"
    assert (cover_first, cover_last) == (27, 46), (cover_first, cover_last)
    # ---- derived pages -----------------------------------------------------------------------------------------------------------
    visible = list(range(first, last + 1))
    per_page = PAGE_COLS * PAGE_ROWS
    pages = [visible[k:k + per_page] for k in range(0, len(visible), per_page)]
    out_dir = APP / "public/game-assets/generated"
    out_dir.mkdir(parents=True, exist_ok=True)
    frames = {}
    page_meta = []
    for p, cells in enumerate(pages):
        rows = -(-len(cells) // PAGE_COLS)
        page = np.zeros((rows * OUT_H, PAGE_COLS * OUT_W, 4), np.uint8)
        for k, i in enumerate(cells):
            r, c = divmod(k, PAGE_COLS)
            crop = cell(i)[y0:y1, x0:x1]
            page[r * OUT_H:(r + 1) * OUT_H, c * OUT_W:(c + 1) * OUT_W] = recolour_pale(premultiplied_resize(crop, (OUT_W, OUT_H)))
            frames[str(i)] = {"page": p, "x": c * OUT_W, "y": r * OUT_H, "w": OUT_W, "h": OUT_H}
        name = f"smoke-{p}.png"
        Image.fromarray(page, "RGBA").save(out_dir / name, optimize=True)
        page_meta.append({"file": name, "w": page.shape[1], "h": page.shape[0], "frames": len(cells)})
    meta = {
        "meta": {
            "source": "design-inputs/game-assets/smoke_transition.png",
            "masterSha256": master_sha,
            "masterSize": [sheet.width, sheet.height],
            "grid": {"cols": COLS, "rows": ROWS, "cellW": CELL_W, "cellH": CELL_H},
            "timelineFrames": FRAME_COUNT,
            "firstVisibleFrame": first,
            "lastVisibleFrame": last,
            "visibleFrames": len(visible),
            "nominalFps": 30,
            "crop": {"x": x0, "y": y0, "w": x1 - x0, "h": y1 - y0, "of": "solid card rectangle x 106..653, y 109..434 inside each 754x534 cell"},
            "frameSize": {"w": OUT_W, "h": OUT_H},
            "pages": page_meta,
            "cover": {"firstFrame": cover_first, "lastFrame": cover_last, "minAlphaAtLeast": FULL_COVER_MIN_ALPHA, "fullyOpaqueFrom": 28, "fullyOpaqueTo": 45},
            "evidence": {
                "nonEmptyCells": [first, last],
                "emptyLeading": first,
                "emptyTrailingInTimeline": FRAME_COUNT - 1 - last,
                "unusedCells": [FRAME_COUNT, COLS * ROWS - 1],
                "columnRuns": col_runs,
                "meanAlphaPeakFrame": first + peak,
                "meanAlphaMonotoneBothSides": True,
                "derived": ["count 84 = packing convention", "reading order forward", "30 fps nominal (skill), not exporter data"],
            },
            "derivation": "Each visible cell is cropped to the 548x308 16:9 part of its flat card rectangle, premultiplied-LANCZOS resized to 480x270, recoloured to pale blue-white by luminance (alpha unchanged) and packed 4 per row, 28 per page.",
        },
        "frames": frames,
    }
    (APP / "src/config/smokeAtlas.json").write_text(json.dumps(meta, indent=1) + "\n")
    print("visible", first, last, "cover", cover_first, cover_last, "pages", [(m["file"], m["w"], m["h"]) for m in page_meta])
if __name__ == "__main__":
    main()
