#!/usr/bin/env python3
"""Build the ONE character atlas (Spec 13 task 13.3) from the real Kenney "Shape Characters" individual PNGs.

SOURCE (read only, never modified):
    design-inputs/game-assets/kenney_shape-characters /PNG/Double/*.png      <- NOTE: the directory name ends in ONE TRAILING SPACE.
It is spelled explicitly below and asserted to exist; nothing is fuzzy-matched or renamed. Individual files are used, not atlas XML crops,
so Default/Double packing differences cannot matter. Double (2x) is used so the heads stay sharp at devicePixelRatio 2.

Every frame below is either (a) an untouched source image or (b) a DERIVATION that is named and explained in `meta.derivations`
of the generated JSON, so a reviewer can tell source art from composition. Nothing is hand-drawn, except the two procedural masks
that are not artwork (halo feather and ground-shadow blur, both computed from the real body silhouette alpha).

Outputs (generated, safe to delete and rebuild):
    public/game-assets/generated/characters.png     the atlas (8 px gutters: the atlas is mip-mapped, see CharacterAssets)
    src/config/characterAtlas.json                  frame rectangles + provenance
Run from web/apps/ghost-lap:  python3 scripts/build-character-atlas.py
"""
import colorsys
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter
from scipy import ndimage

APP = Path(__file__).resolve().parent.parent
ROOT = APP.parents[2]
PACK = ROOT / "design-inputs/game-assets" / "kenney_shape-characters "  # trailing space is REAL
assert PACK.name.endswith(" "), "source folder name must keep its trailing space"
SRC = PACK / "PNG" / "Double"
assert SRC.is_dir(), f"missing {SRC}"
INK = (61, 70, 93)  # the pack's single dark ink colour (pupils, brows, mouths), read from eye_open centre
GUTTER = 8


def load(name: str) -> Image.Image:
    return Image.open(SRC / f"{name}.png").convert("RGBA")


def arr(im: Image.Image) -> np.ndarray:
    return np.array(im, dtype=np.uint8)


frames: dict[str, Image.Image] = {}
derivations: dict[str, str] = {}


def put(key: str, im: Image.Image, how=None):
    frames[key] = im
    if how:
        derivations[key] = how


# ---------------------------------------------------------------- bodies (untouched source) + the P1 comparison options
put("body_p1_yellow", load("yellow_body_circle"))
put("body_p1_red", load("red_body_circle"))
put("body_p2_pink_square", load("pink_body_square"))
put("body_p3_yellow_rhombus", load("yellow_body_rhombus"))
put("body_p4_green_squircle", load("green_body_squircle"))


def hue_shift(im: Image.Image, degrees: float) -> Image.Image:
    """Rotate hue only (saturation and value untouched, alpha untouched). Used for the approved P1 body, see derivations."""
    a = arr(im).astype(np.float64) / 255.0
    out = a.copy()
    flat = a.reshape(-1, 4)
    res = out.reshape(-1, 4)
    for i in range(flat.shape[0]):
        if flat[i, 3] == 0:
            continue
        h, s, v = colorsys.rgb_to_hsv(*flat[i, :3])
        res[i, :3] = colorsys.hsv_to_rgb((h + degrees / 360.0) % 1.0, s, v)
    return Image.fromarray((out * 255 + 0.5).astype(np.uint8), "RGBA")


put(
    "body_p1_orange_derived",
    hue_shift(load("yellow_body_circle"), -13.0),
    "DERIVED, NOT SOURCE ART (APPROVED P1 body, Option C): yellow_body_circle.png with hue rotated -13 degrees (about 40 -> 27 degrees), saturation, value and alpha unchanged. "
    "The owner approved it as the P1 body; it is wired into slot 0. The Kenney source file is unchanged.",
)

# ---------------------------------------------------------------- silhouettes: halo and ground shadow masks (computed from body alpha)
SHAPES = {"circle": "body_p1_yellow", "square": "body_p2_pink_square", "diamond": "body_p3_yellow_rhombus", "squircle": "body_p4_green_squircle"}


def alpha_mask(body: Image.Image, pad: int) -> Image.Image:
    a = body.split()[3]
    canvas = Image.new("L", (body.width + 2 * pad, body.height + 2 * pad), 0)
    canvas.paste(a, (pad, pad))
    return canvas


for shape, body_key in SHAPES.items():
    body = frames[body_key]
    # Halo: silhouette dilated outward then feathered, white, so it keeps the head's own outline (circle/square/diamond/squircle),
    # never a round blob. The runtime multiplies it by a hue tint and a small alpha. 20 px pad each side (of a 160 px head).
    pad = 20
    m = alpha_mask(body, pad)
    grown = m.filter(ImageFilter.MaxFilter(9))  # 4 px dilation
    soft = grown.filter(ImageFilter.GaussianBlur(7))
    h = Image.new("RGBA", m.size, (255, 255, 255, 0))
    h.putalpha(soft)
    put(f"halo_{shape}", h, f"halo mask for {shape}: body alpha, 4 px dilation, 7 px Gaussian feather, white; tinted at runtime")
    # Ground shadow: the same silhouette, small blur, white; tinted slate at runtime and offset bottom-right (light from top-left).
    pad = 8
    m = alpha_mask(body, pad)
    soft = m.filter(ImageFilter.GaussianBlur(4))
    s = Image.new("RGBA", m.size, (255, 255, 255, 0))
    s.putalpha(soft)
    put(f"shadow_{shape}", s, f"ground shadow mask for {shape}: body alpha blurred 4 px, white; tinted and offset at runtime")

# ---------------------------------------------------------------- eyes
eye = arr(load("facial_part_eye_open"))
alpha = eye[..., 3].astype(np.float64) / 255.0
lum = eye[..., :3].astype(np.float64) @ np.array([0.299, 0.587, 0.114])
ink_lum = float(np.dot(INK, [0.299, 0.587, 0.114]))
darkness = np.clip((255.0 - lum) / (255.0 - ink_lum), 0.0, 1.0)  # 0 = white sclera, 1 = ink
sclera = np.zeros_like(eye)
sclera[..., :3] = 255
sclera[..., 3] = eye[..., 3]  # same outer silhouette, interior filled white where the baked pupil was
pupil = np.zeros_like(eye)
pupil[..., :3] = INK
pupil[..., 3] = np.round(darkness * eye[..., 3]).astype(np.uint8)
put("eye_sclera", Image.fromarray(sclera, "RGBA"), "facial_part_eye_open.png: outer silhouette (alpha) filled white where the baked pupil is, so a pupil can move")
put("eye_pupil", Image.fromarray(pupil, "RGBA"), "facial_part_eye_open.png: ink pixels only (white-to-ink antialias becomes alpha), same 40x40 canvas so it is concentric with eye_sclera")
pupil_im = frames["eye_pupil"]
bx0, by0, bx1, by1 = pupil_im.split()[3].point(lambda v: 255 if v > 100 else 0).getbbox()
put("mouth_dot", pupil_im.crop((bx0 - 1, by0 - 1, bx1 + 1, by1 + 1)), "eye_pupil trimmed to its ink disc. Scaled to an oval at runtime: the dark OPEN-MOUTH oval of the release scream, and the small alarmed oval of the fear face (the pack has no standalone oval mouth)")
cy = (by0 + by1) // 2
put("mouth_open_half", pupil_im.crop((bx0 - 1, cy, bx1 + 1, by1 + 1)), "lower half of the eye_pupil ink disc (flat top, round bottom), widened at runtime into the open laughing mouth of the winner face (the pack has no wide open-smile mouth)")
put("eye_closed_up", load("facial_part_eye_closed_up"))      # joyful arc
put("eye_closed_down", load("facial_part_eye_closed_down"))  # squeezed crease

# X eyes: the left X of face_j (connected dark component), cropped. The right X is identical (component sizes 640 = 640).
face_j = arr(load("face_j"))
dark_j = (face_j[..., 3] > 40) & (face_j[..., :3].sum(-1) < 400)
lab, n = ndimage.label(dark_j, structure=np.ones((3, 3)))
comps = []
for i in range(1, n + 1):
    ys, xs = np.where(lab == i)
    comps.append((i, xs.min(), xs.max(), ys.min(), ys.max(), len(xs)))
comps = [c for c in comps if c[5] > 12]
comps.sort(key=lambda c: (c[3] // 20, c[1]))  # brows (top row) then X eyes then mouth; left before right
brow_l, brow_r, x_l, x_r, mouth_j = comps  # asserted by shape below
assert (x_l[5], x_r[5]) == (640, 640) and 60 < brow_l[5] < 1000 and mouth_j[5] == 366, comps
put("eye_x", Image.fromarray(face_j[x_l[3] : x_l[4] + 1, x_l[1] : x_l[2] + 1], "RGBA"), "face_j.png: the left X-eye connected component, cropped (right X is the same pixels)")
put("brow_lift", Image.fromarray(face_j[brow_l[3] : brow_l[4] + 1, brow_l[1] : brow_l[2] + 1], "RGBA"), "face_j.png: the left slim brow component (inner end higher), cropped; the right brow is its mirror")

# ---------------------------------------------------------------- brows (named source pieces) and the heavy worried brow of face_i
put("brow_a", load("facial_part_eyebrow_a"))  # level
put("brow_b", load("facial_part_eyebrow_b"))  # gentle slant, inner end higher when used on the left
face_i = arr(load("face_i"))
dark_i = (face_i[..., 3] > 40) & (face_i[..., :3].sum(-1) < 400)
lab, n = ndimage.label(dark_i, structure=np.ones((3, 3)))
for i in range(1, n + 1):
    ys, xs = np.where(lab == i)
    if (xs.min(), xs.max(), ys.min(), ys.max()) == (0, 49, 0, 26):  # the heavy left brow (measured: 800 px, inner end higher)
        break
else:
    raise SystemExit("face_i heavy left brow component not found")
put("brow_heavy", Image.fromarray(face_i[ys.min() : ys.max() + 1, xs.min() : xs.max() + 1], "RGBA"), "face_i.png: the left heavy brow component (inner end higher), cropped; flipped vertically at runtime it is the tense inward-slanted brow")

# ---------------------------------------------------------------- mouths
put("mouth_sad", load("facial_part_mouth_sad"))
put("mouth_happy", load("facial_part_mouth_happy"))
put("mouth_smirk", load("facial_part_mouth_smirk"))


def stroke_thickness(im: Image.Image) -> int:
    a = arr(im)[..., 3]
    col = a[:, a.shape[1] // 2]
    return int((col > 128).sum())


thick = stroke_thickness(frames["mouth_happy"])
brow_a = frames["brow_a"]
flat = brow_a.resize((30, thick), Image.LANCZOS)
put("mouth_flat", flat, f"NO flat neutral mouth exists in the pack. facial_part_eyebrow_a.png (a level flat dark bar with round ends) resampled to 30 x {thick} px, the stroke weight of facial_part_mouth_happy.png")
happy = frames["mouth_happy"]
put("mouth_grin", happy.resize((round(happy.width * 1.45), round(happy.height * 1.45)), Image.LANCZOS), "NO broader grin exists in the pack. facial_part_mouth_happy.png scaled uniformly x1.45 (a wider, deeper curve with a heavier stroke)")

# ---------------------------------------------------------------- the permanent evil ghost face: face_f brow+narrowed-eye units
face_f = load("face_f")
put("ghost_eye_brow", face_f.crop((0, 0, 54, 46)), "face_f.png: the left angry-brow + narrowed-eye unit (x 0..53, y 0..45; the right unit is its exact mirror), mouth excluded")

# ---------------------------------------------------------------- pack with gutters
order = sorted(frames.items(), key=lambda kv: (-kv[1].height, kv[0]))
ATLAS_W = 1024
x = y = row_h = 0
rects = {}
for key, im in order:
    w, h = im.size
    if x + w + GUTTER > ATLAS_W:
        x, y, row_h = 0, y + row_h + GUTTER, 0
    rects[key] = (x + GUTTER // 2, y + GUTTER // 2, w, h)
    x += w + GUTTER
    row_h = max(row_h, h + 0)
ATLAS_H = y + row_h + GUTTER
atlas = Image.new("RGBA", (ATLAS_W, ATLAS_H), (0, 0, 0, 0))
for key, im in frames.items():
    rx, ry, _, _ = rects[key]
    atlas.paste(im, (rx, ry))
out_png = APP / "public/game-assets/generated/characters.png"
out_png.parent.mkdir(parents=True, exist_ok=True)
atlas.save(out_png, optimize=True)


def centre_colour(key: str):
    im = frames[key]
    return "#%02x%02x%02x" % im.getpixel((im.width // 2, im.height // 2))[:3]


meta = {
    "meta": {
        "generator": "scripts/build-character-atlas.py",
        "source": "design-inputs/game-assets/kenney_shape-characters /PNG/Double (Kenney Shape Characters 1.0, CC0; note the trailing space in the folder name)",
        "image": "characters.png",
        "size": {"w": ATLAS_W, "h": ATLAS_H},
        "unit": "Double-resolution source pixels. A head body is 160 x 160.",
        "bodyCentreColours": {k: centre_colour(k) for k in frames if k.startswith("body_")},
        "derivations": derivations,
    },
    "frames": {k: {"x": r[0], "y": r[1], "w": r[2], "h": r[3]} for k, r in sorted(rects.items())},
}
(APP / "src/config/characterAtlas.json").write_text(json.dumps(meta, indent=1) + "\n")
print(f"atlas {ATLAS_W}x{ATLAS_H}, {len(frames)} frames, mouth stroke {thick}px")
for k, v in meta["meta"]["bodyCentreColours"].items():
    print(k, v)
