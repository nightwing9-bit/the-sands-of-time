// Reduced-motion switch for the transient effects. The operating-system preference is the default; a later Settings screen (13.9) or a
// preview toggle can override it. Effects ask at the moment they start, so a change applies to the next effect and never mid-effect.
let override: boolean | null = null;
export function prefersReducedMotion(): boolean {
  if (override !== null) return override;
  return typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}
/** null returns control to the operating-system preference. */
export function setReducedMotionOverride(value: boolean | null): void { override = value; }
export function reducedMotionOverride(): boolean | null { return override; }
