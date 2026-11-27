#!/usr/bin/env python3
"""Build the one grey-wall atlas from the Kenney Pixel Platformer Blocks "Rock" tiles (the pack's native blue-grey material).

Source tiles are only READ from design-inputs/game-assets/kenney_pixel-platformer-blocks/Tiles/Rock (CC0, 18x18, never modified).
Each source tile has exactly four colours. They are remapped one-to-one (edge, shade, body, highlight) to a slightly deeper slate so
the walls hold against the pale snow; bevel, alpha and pixel structure are untouched. No per-pixel blur, no flattening.

Block A = rows 3-5 (clean); F1-F3 = A plus sparse softened flecks taken from the speckled interior tile (row 7, col 1). Edges/corners use cols 3-5 (heavy lit bevel), the interior cell uses col 1. Each tile is upscaled 2x with nearest neighbour (36 px) and given a 2 px edge-extruded gutter so linear sampling
never bleeds a neighbour. 18x18 is texture resolution only; world size comes from the ArenaV1 rectangles.

Outputs (generated; safe to delete and rebuild):
  public/game-assets/generated/grey-walls.png
  src/config/greyWallsAtlas.json   (frame rectangles, same coordinates the PNG uses)
Run from web/apps/ghost-lap:  python3 scripts/build-wall-atlas.py
"""
import json
from pathlib import Path
from PIL import Image

APP = Path(__file__).resolve().parent.parent
SRC = APP.parents[2] / "design-inputs/game-assets/kenney_pixel-platformer-blocks/Tiles/Rock"
SCALE, PAD, SRC_PX = 2, 2, 18
CELL = SRC_PX * SCALE + PAD * 2

# Source palette (verified: only these four colours occur in Rock tiles 27-47 and 54-80) -> slate target.
REMAP = {
    (67, 74, 95): (0x3B, 0x45, 0x58),     # outline / deepest edge
    (114, 120, 144): (0x5F, 0x6B, 0x7E),  # shaded bevel
    (159, 165, 189): (0x7A, 0x86, 0x97),  # body
    (220, 225, 231): (0xC3, 0xCC, 0xD7),  # lit bevel
}
POS = ["tl", "t", "tr", "l", "c", "r", "bl", "b", "br"]
BLOCKS = {"A": 3, "F1": 3, "F2": 3, "F3": 3}  # first tile row of the Kenney block; F1-F3 are A plus sparse stone flecks (see fleck())


def tile(index: int) -> Image.Image:
    im = Image.open(SRC / f"tile_{index:04d}.png").convert("RGBA")
    out = Image.new("RGBA", im.size)
    px, dst = im.load(), out.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            key = (r, g, b)
            if key not in REMAP:
                raise SystemExit(f"unexpected colour {key} in tile_{index:04d}; remap table must be extended deliberately")
            dst[x, y] = (*REMAP[key], a)
    return out


BODY = REMAP[(159, 165, 189)]
# Subtle fleck colours: the Kenney speckle pixels (lit / shaded) pulled most of the way back toward the body so they stay low-contrast.
SOFT = {REMAP[(220, 225, 231)]: (0x92, 0x9D, 0xAD), REMAP[(114, 120, 144)]: (0x6C, 0x78, 0x8A), REMAP[(67, 74, 95)]: (0x6C, 0x78, 0x8A)}
FLECK_SRC = tile(9 * 7 + 1)  # Kenney speckled interior tile (rows 6-8, col 1): the only source of fleck pixels
def fleck(base: Image.Image, k: int) -> Image.Image:
    """A tile + Kenney flecks stamped ONLY on plain body pixels (bevel/outline untouched, so bevels stay continuous). Variant k uses a different
    wrapped window of the speckle tile, so no identical cluster repeats in neighbouring cells."""
    out = base.copy()
    bp, fp, op = base.load(), FLECK_SRC.load(), out.load()
    dx, dy = (5 * k + 3) % SRC_PX, (7 * k + 1) % SRC_PX
    for y in range(SRC_PX):
        for x in range(SRC_PX):
            f = fp[(x + dx) % SRC_PX, (y + dy) % SRC_PX][:3]
            if f != BODY and f in SOFT and bp[x, y][:3] == BODY and 1 < x < SRC_PX - 2 and 1 < y < SRC_PX - 2:
                op[x, y] = (*SOFT[f], 255)
    return out
atlas = Image.new("RGBA", (CELL * 3, CELL * 12), (0, 0, 0, 0))
frames = {}
for bi, (name, row0) in enumerate(BLOCKS.items()):
    for n, pos in enumerate(POS):
        r, c = divmod(n, 3)
        # Edges and corners come from the heavier-bevel block (columns 3-5); the interior cell from the plain block (columns 0-2) so the
        # 80x80 centre block does not repeat the source's single inset square four times.
        col = 1 if pos == "c" else 3 + c
        base = tile(9 * (row0 + r) + col)
        if name != "A" and pos != "c":
            base = fleck(base, int(name[1:]) + 2 * n)
        elif name != "A":
            base = fleck(base, int(name[1:]) + 2 * n)
        up = base.resize((SRC_PX * SCALE,) * 2, Image.NEAREST)
        cell = Image.new("RGBA", (CELL, CELL), (0, 0, 0, 0))
        cell.paste(up, (PAD, PAD))
        # edge extrusion into the gutter
        for g in range(PAD):
            cell.paste(up.crop((0, 0, 1, up.height)), (g, PAD))
            cell.paste(up.crop((up.width - 1, 0, up.width, up.height)), (PAD + up.width + g, PAD))
        for g in range(PAD):
            cell.paste(cell.crop((0, PAD, CELL, PAD + 1)), (0, g))
            cell.paste(cell.crop((0, PAD + up.height - 1, CELL, PAD + up.height)), (0, PAD + up.height + g))
        ox, oy = c * CELL, (bi * 3 + r) * CELL
        atlas.paste(cell, (ox, oy))
        frames[f"{name}/{pos}"] = {"x": ox + PAD, "y": oy + PAD, "w": up.width, "h": up.height}

out_png = APP / "public/game-assets/generated/grey-walls.png"
out_png.parent.mkdir(parents=True, exist_ok=True)
atlas.save(out_png, optimize=True)
meta = {
    "image": "/game-assets/generated/grey-walls.png",
    "size": {"w": atlas.width, "h": atlas.height},
    "source": "kenney_pixel-platformer-blocks/Tiles/Rock rows 3-8 (CC0); F1-F3 = A tiles with Kenney speckle pixels softened onto body pixels",
    "sourceTilePx": SRC_PX,
    "upscale": SCALE,
    "frames": frames,
}
(APP / "src/config/greyWallsAtlas.json").write_text(json.dumps(meta, indent=1) + "\n")
print(f"wrote {out_png} {atlas.size} and {len(frames)} frames")
