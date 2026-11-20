// The four approved actors and the face geometry, in ONE place. Every number below is a visual tuning value, not game truth.
// Units: atlas (Double) pixels relative to the head centre, y down. A head body is 160 x 160 in these units.
import type { ActorSlot } from "../presentation/types.ts";
import type { BodyKey, FrameKey, ShapeId } from "./CharacterAssets.ts";
import type { FaceId } from "./expression.ts";

export interface SlotSpec {
  readonly slot: ActorSlot;
  readonly shape: ShapeId;
  readonly body: BodyKey;
  /** Halo and label hue, the body's own centre colour (from characterAtlas.json meta.bodyCentreColours). */
  readonly hue: number;
  /** The face is drawn a little smaller on the diamond so brows and mouth stay inside the four points; the rest need less. */
  readonly faceScale: number;
  readonly faceDy: number;
  readonly name: string;
}

export const SLOT_SPECS: Readonly<Record<ActorSlot, SlotSpec>> = {
  // P1: APPROVED Option C, yellow_body_circle.png hue-rotated -13 degrees at atlas build time (see P1_OPTIONS). Kenney source file unchanged.
  0: { slot: 0, shape: "circle", body: "body_p1_orange_derived", hue: 0xfa8e39, faceScale: 1, faceDy: 2, name: "P1 · circle" },
  1: { slot: 1, shape: "square", body: "body_p2_pink_square", hue: 0xff85c7, faceScale: 0.98, faceDy: 2, name: "P2 · square" },
  2: { slot: 2, shape: "diamond", body: "body_p3_yellow_rhombus", hue: 0xfab839, faceScale: 0.8, faceDy: 4, name: "P3 · diamond" },
  3: { slot: 3, shape: "squircle", body: "body_p4_green_squircle", hue: 0x64bf77, faceScale: 0.94, faceDy: 2, name: "P4 · squircle" },
};

/** P1 colour options, all from the real pack. C is the APPROVED P1 (wired into SLOT_SPECS[0]); A and B stay in the viewer comparison as not selected. */
export const P1_OPTIONS = [
  { id: "yellow", body: "body_p1_yellow", hue: 0xfab839, label: "A · yellow_body_circle.png, unmodified (not selected)", derived: false },
  { id: "red", body: "body_p1_red", hue: 0xda5463, label: "B · red_body_circle.png, unmodified (not selected)", derived: false },
  { id: "orange", body: "body_p1_orange_derived", hue: 0xfa8e39, label: "C · yellow_body_circle.png hue-rotated -13°, derived at build time (APPROVED P1)", derived: true },
] as const satisfies readonly { id: string; body: BodyKey; hue: number; label: string; derived: boolean }[];

/** Visual head size options in world units. Visual policy only: the authoritative collision body stays 48x48 on the server. */
export const HEAD_SIZE_OPTIONS = [
  { units: 64, label: "64 (approved default)" },
  { units: 56, label: "56 (first fallback)" },
  { units: 48, label: "48 (collider-matching, second fallback)" },
] as const;
/** Default visual head size: 64. Fall back to 56, then 48, only if real gameplay shows unacceptable wall overlap or visual-collision mismatch. */
export const HEAD_UNITS: number = HEAD_SIZE_OPTIONS[0].units;
/** Halo alpha by charge (skill ref 06 DIRECTOR BASELINE seeds). 0 and 1 are identical: no halo. */
export const HALO_ALPHA: readonly [number, number, number, number, number] = [0, 0, 0.15, 0.26, 0.95];
/** Scale of the permanent 4/4 halo (13.6 refinement): matches an early developed frame of the Burst pulse (about 150 ms: 1.10 of its 1.16 peak). Charges 0..3 stay 1. */
export const HALO_SCALE: readonly [number, number, number, number, number] = [1, 1, 1, 1, 1.1];
/** Pupil travel inside a 40 px eye (24 px pupil): at most 8 px of white ring, minus a 1 px safety. */
export const GAZE_TRAVEL = 7;

export interface PartSpec {
  readonly frame: FrameKey;
  readonly x: number;
  readonly y: number;
  /** Negative sx mirrors a part (right brows and the right ghost eye are mirrors of the left source pixels). */
  readonly sx?: number;
  readonly sy?: number;
  readonly rot?: number;
  /** Marks a pupil: it follows the gaze, within `GAZE_TRAVEL * travel` of its base position. */
  readonly gaze?: number;
}

const EYE_X = 30;
const BROW_X = 34;
const eyes = (k = 1, pupilK = 1, gaze = true, y = 0): PartSpec[] => [-EYE_X, EYE_X].flatMap((x): PartSpec[] => [
  { frame: "eye_sclera", x, y, sx: k, sy: k },
  { frame: "eye_pupil", x, y, sx: k * pupilK, sy: k * pupilK, ...(gaze ? { gaze: k } : {}) },
]);
/** Left brow as drawn in the source, right brow its mirror. */
const brows = (frame: FrameKey, y: number, k = 1, x = BROW_X, rotL = 0, rotR = 0, yR = y): PartSpec[] => [
  { frame, x: -x, y, sx: k, sy: k, rot: rotL },
  { frame, x, y: yR, sx: -k, sy: k, rot: rotR },
];

/** Every face as source pieces. See characterAtlas.json meta.derivations for which frames are untouched pack art and which are derived. */
export const FACE_RECIPES: Readonly<Record<FaceId, readonly PartSpec[]>> = {
  // 0/4 and 1/4: worried open eyes, brows slanting up toward the centre, a clearly downward mouth.
  frown: [...eyes(), ...brows("brow_b", -33), { frame: "mouth_sad", x: 0, y: 33 }],
  // 2/4: plain open eyes, level brows, a short flat mouth (derived: the pack has none).
  neutral: [...eyes(), ...brows("brow_a", -33), { frame: "mouth_flat", x: 0, y: 33 }],
  // 3/4: brighter eyes, brows slightly lifted and level, an upward smile.
  happy: [...eyes(1.06), ...brows("brow_a", -37), { frame: "mouth_happy", x: 0, y: 33 }],
  // 4/4: wide lively eyes, clearly lifted brows with the inner ends a touch higher (open and cheerful; outer-higher would read as scheming), a broader deeper grin (derived from mouth_happy).
  grin: [...eyes(1.13), ...brows("brow_a", -45, 1.05, BROW_X, -0.13, 0.13), { frame: "mouth_grin", x: 0, y: 34 }],
  // 4/4 release: eyes squeezed into creases, brows tensed inward, a wide dark open oval.
  release: [
    { frame: "eye_closed_down", x: -EYE_X, y: 2 }, { frame: "eye_closed_down", x: EYE_X, y: 2 },
    { frame: "brow_heavy", x: -27, y: -26, sy: -1 }, { frame: "brow_heavy", x: 27, y: -26, sx: -1, sy: -1 },
    { frame: "mouth_dot", x: 0, y: 35, sx: 1.35, sy: 1.75 },
  ],
  // Burst hit: X eyes, brows knocked out of alignment (one high and tilted up, one low and tilted down), a short crooked pained mouth.
  hit: [
    { frame: "eye_x", x: -32, y: 4 }, { frame: "eye_x", x: 32, y: 4 },
    ...brows("brow_lift", -46, 1, 34, -0.12, -0.5, -30),
    { frame: "mouth_smirk", x: 3, y: 38, sx: 1.25, sy: -1.25, rot: 0.12 },
  ],
  // Weaker-contact fear: wide eyes with small centred pupils (no gaze), brows raised and angled up toward the centre, a small alarmed oval.
  fear: [
    ...eyes(1.16, 0.5, false),
    ...brows("brow_heavy", -44, 0.88),
    { frame: "mouth_dot", x: 0, y: 36, sx: 0.62, sy: 0.85 },
  ],
  // Winner: joyful closed arcs, raised brows, a wide open laughing mouth (derived from the lower half of a pupil disc).
  winner: [
    { frame: "eye_closed_up", x: -EYE_X, y: 0 }, { frame: "eye_closed_up", x: EYE_X, y: 0 },
    ...brows("brow_lift", -42),
    { frame: "mouth_open_half", x: 0, y: 26, sx: 1.9, sy: 1.6 },
  ],
  // The permanent ghost face: heavy inward-slanted brows over narrowed glaring eyes (face_f units), a crooked smirk.
  evil: [
    { frame: "ghost_eye_brow", x: -28, y: -8 }, { frame: "ghost_eye_brow", x: 28, y: -8, sx: -1 },
    { frame: "mouth_smirk", x: 6, y: 34, sx: 1.2, sy: 1.2 },
  ],
};
