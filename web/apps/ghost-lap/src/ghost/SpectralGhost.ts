// The historical ghost (13.5): the SAME selected head as the living player (the real CharacterView body + the permanent evil face),
// recoloured by ONE spectral luminance shader and given a subtle bright edge. Presentation only.
//
//   * Not interactive. A GhostView has no collider, no pickup, no score, no event hook: it is a sprite at a position the caller got from a
//     verified history trace tick. It never collects candles and never counts as a living player.
//   * Not a light source. It sits in the actors layer above the lightmap and is never lit or graded.
//   * No body blur, no trail, no jitter. The only decoration is the edge, computed from the head's own silhouette.
//   * The expression is the permanent evil face (CharacterView kind "ghost" ignores charge and every override), whatever the source run did.
//
// COST: the ghost head never changes (one slot, one face), so the shader runs ONCE per (slot, size) into a small texture and the ghost is then
// a plain Sprite. There is no per-frame filter pass at all.
import { Container, Filter, Rectangle, Sprite, Texture, type Renderer } from "pixi.js";
import { CharacterView } from "../characters/CharacterView.ts";
import type { CharacterTextures } from "../characters/CharacterAssets.ts";
import { SPECTRAL } from "../lighting/lightingPresets.ts";
import type { ActorSlot } from "../presentation/types.ts";

/** Texels per world unit of the baked ghost. The head is shown at about 1.2 to 2.4 screen px per unit, so 3 keeps it sharp on a retina display. */
const BAKE_RESOLUTION = 3;
/** Extra transparent room around the head for the outer edge, world units. */
const BAKE_MARGIN = 8;

const VERTEX = /* glsl */ `
in vec2 aPosition;
out vec2 vTextureCoord;
uniform vec4 uInputSize;
uniform vec4 uOutputFrame;
uniform vec4 uOutputTexture;
vec4 filterVertexPosition(void) {
  vec2 position = aPosition * uOutputFrame.zw + uOutputFrame.xy;
  position.x = position.x * (2.0 / uOutputTexture.x) - 1.0;
  position.y = position.y * (2.0 * uOutputTexture.z / uOutputTexture.y) - uOutputTexture.z;
  return vec4(position, 0.0, 1.0);
}
vec2 filterTextureCoord(void) { return aPosition * (uOutputFrame.zw * uInputSize.zw); }
void main(void) {
  gl_Position = filterVertexPosition();
  vTextureCoord = filterTextureCoord();
}
`;

// Luminance L = 0.2126 R + 0.7152 G + 0.0722 B of the un-premultiplied head+face pixel. Dark features (brows, mouth) map to the deep teal,
// body midtones to translucent cyan, highlights to near-white cyan: all four body hues land on the same cold palette and the face keeps its
// definition. The edge is outside-in: the largest neighbour alpha on two rings minus this pixel's alpha, so it hugs the real silhouette.
const FRAGMENT = /* glsl */ `
in vec2 vTextureCoord;
out vec4 finalColor;
uniform sampler2D uTexture;
uniform highp vec4 uInputSize; // highp: the vertex stage declares it highp and WebGL requires the precisions to match
uniform vec3 uDark;
uniform vec3 uMid;
uniform vec3 uHigh;
uniform vec3 uEdge;
uniform float uBodyAlpha;
uniform float uEdgeAlpha;
uniform float uEdgeRadius;

float alphaAt(vec2 uv) { return texture(uTexture, uv).a; }

void main(void) {
  vec4 c = texture(uTexture, vTextureCoord);
  float a = c.a;
  vec3 rgb = a > 0.0 ? c.rgb / a : vec3(0.0);
  float L = clamp((dot(rgb, vec3(0.2126, 0.7152, 0.0722)) - 0.12) / 0.78, 0.0, 1.0);
  vec3 body = L < 0.55 ? mix(uDark, uMid, smoothstep(0.0, 0.55, L)) : mix(uMid, uHigh, smoothstep(0.55, 1.0, L));

  vec2 px = uInputSize.zw;
  float outer = 0.0;
  float inner = 1.0;
  for (int i = 0; i < 8; i++) {
    float ang = float(i) * 0.785398;
    vec2 dir = vec2(cos(ang), sin(ang));
    float a1 = alphaAt(vTextureCoord + dir * px * uEdgeRadius * 0.5);
    float a2 = alphaAt(vTextureCoord + dir * px * uEdgeRadius);
    outer = max(outer, max(a1, a2 * 0.8));
    inner = min(inner, a1);
  }
  float rim = clamp(outer - a, 0.0, 1.0) * uEdgeAlpha;            // glow just outside the silhouette
  float lip = a * (1.0 - inner) * uEdgeAlpha * 0.55;               // slightly brighter inside edge
  float bodyA = a * uBodyAlpha;

  vec3 bodyRgb = mix(body, uEdge, lip);
  float outA = bodyA + rim * (1.0 - bodyA);
  vec3 premult = bodyRgb * bodyA + uEdge * rim * (1.0 - bodyA);
  finalColor = vec4(premult, outA);
}
`;

const rgb = (hex: number): [number, number, number] => [((hex >> 16) & 255) / 255, ((hex >> 8) & 255) / 255, (hex & 255) / 255];

/** The one spectral material. Single pass, no extra textures. */
export function createSpectralGhostFilter(edgeRadiusTexels: number): Filter {
  return Filter.from({
    gl: { vertex: VERTEX, fragment: FRAGMENT, name: "spectral-ghost" },
    resources: {
      ghostUniforms: {
        uDark: { value: rgb(SPECTRAL.dark), type: "vec3<f32>" },
        uMid: { value: rgb(SPECTRAL.mid), type: "vec3<f32>" },
        uHigh: { value: rgb(SPECTRAL.high), type: "vec3<f32>" },
        uEdge: { value: rgb(SPECTRAL.edge), type: "vec3<f32>" },
        uBodyAlpha: { value: SPECTRAL.bodyAlpha, type: "f32" },
        uEdgeAlpha: { value: SPECTRAL.edgeAlpha, type: "f32" },
        uEdgeRadius: { value: edgeRadiusTexels, type: "f32" },
      },
    },
    padding: Math.ceil(edgeRadiusTexels) + 2,
    resolution: BAKE_RESOLUTION,
  });
}

/** Baked ghost textures, one per (slot, head size), shared by every ghost view. Owned by the scene; destroy() frees them. */
export class GhostTextures {
  private readonly baked = new Map<string, Texture>();
  private destroyed = false;
  constructor(private readonly renderer: Renderer, private readonly characters: CharacterTextures) {}

  texture(slot: ActorSlot, headUnits: number): Texture {
    const key = `${slot}:${headUnits}`;
    let texture = this.baked.get(key);
    if (!texture) {
      texture = this.bake(slot, headUnits);
      this.baked.set(key, texture);
    }
    return texture;
  }

  private bake(slot: ActorSlot, headUnits: number): Texture {
    const view = new CharacterView(this.characters, { slot, kind: "ghost", headUnits });
    view.update(0, 16); // settle the (single) evil rig to full alpha; a ghost never changes face afterwards
    const holder = new Container({ label: "ghost-bake" });
    holder.addChild(view.head);
    const filter = createSpectralGhostFilter(SPECTRAL.edgeUnits * BAKE_RESOLUTION);
    holder.filters = [filter];
    const half = headUnits / 2 + BAKE_MARGIN;
    const texture = this.renderer.generateTexture({
      target: holder,
      frame: new Rectangle(-half, -half, half * 2, half * 2),
      resolution: BAKE_RESOLUTION,
      antialias: true,
    });
    holder.filters = [];
    filter.destroy();
    holder.destroy({ children: true });
    view.destroy();
    return texture;
  }

  destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    for (const t of this.baked.values()) t.destroy(true);
    this.baked.clear();
  }
}

/** One ghost on screen. A plain sprite: put it in a layer BELOW the living heads and set its position from a verified history tick. */
export class GhostView {
  readonly root = new Container({ label: "ghost" });
  private readonly sprite: Sprite;
  constructor(textures: GhostTextures, readonly slot: ActorSlot, headUnits: number) {
    this.sprite = new Sprite(textures.texture(slot, headUnits));
    this.sprite.anchor.set(0.5);
    this.root.addChild(this.sprite);
    this.root.eventMode = "none"; // not interactive, by construction
  }
  /** World units, exactly the historical position. Never offset to show divergence: separation comes from the data alone. */
  setPosition(x: number, y: number): void { this.root.position.set(x, y); }
  destroy(): void { this.root.destroy({ children: true }); } // the baked texture stays with GhostTextures
}
