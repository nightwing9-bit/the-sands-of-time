// The Sands of Time hourglass. Glass with a lit left edge, slate caps, warm sand. Sand below = history already elapsed, sand above = what
// remains, so the glass says the same thing as the track beside it. Presentation only: `elapsed` is a 0..1 fraction the caller derives.
import { useId } from "react";

const TOP = 11; // inner glass top
const NECK = 56;
const BOTTOM = 101;
const UPPER = "M14 11 C14 34 30 44 33 56 L39 56 C42 44 58 34 58 11 Z";
const LOWER = "M33 56 C30 68 14 78 14 101 L58 101 C58 78 42 68 39 56 Z";

/**
 * `flowing` (default true) lets the thin falling thread animate. The history dock sets it only while time is actually moving (playback,
 * loading), so a still moment is a still hourglass. Reduced motion stops the animation in CSS either way.
 */
export function HourglassMark({ elapsed, label, flowing = true }: { elapsed: number; label?: string; flowing?: boolean }) {
  const id = useId();
  const e = Math.min(1, Math.max(0, elapsed));
  const upperTop = NECK - (NECK - TOP) * 0.96 * Math.pow(1 - e, 0.7); // upper sand surface; eased so the funnel reads as the share left
  const moundHeight = (BOTTOM - NECK) * 0.95 * Math.pow(e, 0.8); // lower sand apex height above the base
  const streaming = e > 0 && e < 1;
  const surfaceRx = 5 + 17 * ((NECK - upperTop) / (NECK - TOP)); // how wide the upper sand's lit surface reads: a wider funnel when fuller
  return (
    <svg className="gl-hourglass" viewBox="0 0 72 112" role="img" aria-label={label ?? "Sands of Time hourglass"} focusable="false" data-flowing={flowing && streaming ? "1" : "0"}>
      <defs>
        <linearGradient id={`${id}-glass`} x1="0" y1="0" x2="1" y2="0">
          <stop offset="0" stopColor="#ffffff" stopOpacity="0.75" />
          <stop offset="0.35" stopColor="#ffffff" stopOpacity="0.18" />
          <stop offset="1" stopColor="#bcd6ea" stopOpacity="0.34" />
        </linearGradient>
        <linearGradient id={`${id}-sand`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" style={{ stopColor: "var(--gl-sand-gold)" }} />
          <stop offset="1" style={{ stopColor: "var(--gl-sand-gold-deep)" }} />
        </linearGradient>
        <linearGradient id={`${id}-cap`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#5b6b7e" />
          <stop offset="1" style={{ stopColor: "var(--gl-stone-dark)" }} />
        </linearGradient>
        <clipPath id={`${id}-up`}><path d={UPPER} /></clipPath>
        <clipPath id={`${id}-low`}><path d={LOWER} /></clipPath>
      </defs>
      <ellipse cx="36" cy="108" rx="24" ry="2.6" fill="rgba(38,60,75,0.18)" />
      <g clipPath={`url(#${id}-up)`}><rect x="10" y={upperTop} width="52" height={NECK - upperTop + 1} fill={`url(#${id}-sand)`} /></g>
      <g clipPath={`url(#${id}-low)`}>
        <path d={`M10 ${BOTTOM + 1} L10 ${BOTTOM} Q36 ${BOTTOM - moundHeight * 2} 62 ${BOTTOM} L62 ${BOTTOM + 1} Z`} fill={`url(#${id}-sand)`} />
      </g>
      {/* Depth: the lit surface of the upper sand and a soft crest on the lower mound. */}
      {e < 0.995 ? <ellipse cx="36" cy={upperTop} rx={surfaceRx} ry="1.7" fill="#f4dcae" opacity="0.8" clipPath={`url(#${id}-up)`} /> : null}
      {e > 0.02 ? <ellipse cx="36" cy={BOTTOM - moundHeight + 0.4} rx={Math.min(9, 2 + moundHeight * 0.5)} ry="1.1" fill="#f4dcae" opacity="0.7" clipPath={`url(#${id}-low)`} /> : null}
      {streaming ? <line className="gl-hourglass-stream" x1="36" y1={NECK - 2} x2="36" y2={BOTTOM - moundHeight + 1} /> : null}
      <path d={UPPER} fill={`url(#${id}-glass)`} stroke="rgba(38,60,75,0.38)" strokeWidth="1.2" />
      <path d={LOWER} fill={`url(#${id}-glass)`} stroke="rgba(38,60,75,0.38)" strokeWidth="1.2" />
      {/* The far wall of the glass, seen through the near one: the quiet inner edge that reads as thickness. */}
      <path d="M56 17 C56 34 45 43 41 53" fill="none" stroke="rgba(38,60,75,0.22)" strokeWidth="1" strokeLinecap="round" />
      <path d="M56 95 C56 82 48 74 42 66" fill="none" stroke="rgba(38,60,75,0.2)" strokeWidth="1" strokeLinecap="round" />
      <path d="M17 16 C17 33 28 41 31 52" fill="none" stroke="#ffffff" strokeOpacity="0.9" strokeWidth="1.8" strokeLinecap="round" />
      <path d="M17 96 C17 82 24 74 30 66" fill="none" stroke="#ffffff" strokeOpacity="0.55" strokeWidth="1.4" strokeLinecap="round" />
      <rect x="8" y="3" width="56" height="8" rx="3.5" fill={`url(#${id}-cap)`} />
      <rect x="8" y="101" width="56" height="8" rx="3.5" fill={`url(#${id}-cap)`} />
      {/* Cap faces: a lit top edge on the upper cap and the lower cap's near lip, so the frame has a front and a top. */}
      <path d="M10 5.2 H62" stroke="#ffffff" strokeOpacity="0.38" strokeWidth="0.9" strokeLinecap="round" />
      <path d="M10 102.4 H62" stroke="#ffffff" strokeOpacity="0.22" strokeWidth="0.9" strokeLinecap="round" />
      <path d="M11.5 4.4 H60.5" stroke="var(--gl-sand-gold)" strokeWidth="1" strokeLinecap="round" opacity="0.9" />
      <path d="M11.5 107.6 H60.5" stroke="var(--gl-sand-gold)" strokeWidth="1" strokeLinecap="round" opacity="0.55" />
    </svg>
  );
}
