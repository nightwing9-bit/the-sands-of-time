import { useId, type CSSProperties } from "react";

/**
 * The Sands of Time hourglass, drawn for Revenant's dark-teal dock: pale glass with a lit left edge, a pewter-teal frame and warm
 * muted sand. Sand below is the history already passed (the handle's position), sand above is what is left. `flowing` animates the
 * thread only while time is actually moving. Presentation only; `elapsed` is a 0..1 fraction the caller derives.
 */
const TOP = 15.5, NECK = 50, BOTTOM = 84.5;
const UPPER = "M17 24 C17 18 20.5 15.5 26 15.5 H38 C43.5 15.5 47 18 47 24 C47 36 33.8 41 33.6 50 H30.4 C30.2 41 17 36 17 24 Z";
const LOWER = "M30.4 50 C30.2 59 17 64 17 76 C17 82 20.5 84.5 26 84.5 H38 C43.5 84.5 47 82 47 76 C47 64 33.8 59 33.6 50 Z";

export function SandGlass({ elapsed, flowing, burst = 0, className }: { elapsed: number; flowing: boolean; burst?: number; className?: string }) {
  const id = useId().replace(/:/g, "");
  const e = Math.min(1, Math.max(0, elapsed));
  const surface = NECK - (NECK - TOP - 0.6) * Math.pow(1 - e, 0.8);
  const dip = Math.min(2.6, (NECK - surface) * 0.5) * (e < 1 ? 1 : 0);
  const apex = BOTTOM - (BOTTOM - NECK - 2) * Math.pow(e, 0.8);
  const shoulder = BOTTOM - 18 * Math.pow(e, 1.1);
  const streaming = e > 0.002 && e < 0.998;
  const url = (n: string) => `url(#${id}-${n})`;
  return <svg className={"r8-sg" + (flowing && streaming ? " is-flowing" : "") + (className ? " " + className : "")} viewBox="0 0 64 100" aria-hidden="true" focusable="false">
    <defs>
      <linearGradient id={`${id}-sand`} gradientUnits="userSpaceOnUse" x1="0" y1="16" x2="0" y2="85">
        <stop offset="0" stopColor="#F1DFB4" /><stop offset=".45" stopColor="#D9B676" /><stop offset="1" stopColor="#B38848" /></linearGradient>
      <linearGradient id={`${id}-shade`} x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stopColor="#3A2608" stopOpacity=".42" /><stop offset=".3" stopColor="#3A2608" stopOpacity="0" />
        <stop offset=".62" stopColor="#3A2608" stopOpacity="0" /><stop offset="1" stopColor="#3A2608" stopOpacity=".5" /></linearGradient>
      <linearGradient id={`${id}-glass`} x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stopColor="#EAF8F6" stopOpacity=".5" /><stop offset=".28" stopColor="#CDE8E6" stopOpacity=".12" />
        <stop offset=".7" stopColor="#CDE8E6" stopOpacity=".05" /><stop offset="1" stopColor="#CDE8E6" stopOpacity=".24" /></linearGradient>
      <linearGradient id={`${id}-rim`} x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#F4FBFA" stopOpacity=".85" /><stop offset=".5" stopColor="#A9D4D6" stopOpacity=".42" /><stop offset="1" stopColor="#F4FBFA" stopOpacity=".7" /></linearGradient>
      <linearGradient id={`${id}-metal`} x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stopColor="#B9D8DA" /><stop offset=".4" stopColor="#6FA1A8" /><stop offset="1" stopColor="#2B6470" /></linearGradient>
      <linearGradient id={`${id}-post`} x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stopColor="#A9D0D3" /><stop offset=".5" stopColor="#4D868F" /><stop offset="1" stopColor="#1E5560" /></linearGradient>
      <radialGradient id={`${id}-halo`} cx=".5" cy=".5" r=".5"><stop offset="0" stopColor="#8FD0D4" stopOpacity=".34" /><stop offset="1" stopColor="#8FD0D4" stopOpacity="0" /></radialGradient>
      <filter id={`${id}-blur`} x="-30%" y="-200%" width="160%" height="500%"><feGaussianBlur stdDeviation="1.5" /></filter>
      <clipPath id={`${id}-up`}><path d={UPPER} /></clipPath>
      <clipPath id={`${id}-low`}><path d={LOWER} /></clipPath>
    </defs>
    <ellipse cx="32" cy="50" rx="31" ry="46" fill={url("halo")} />
    <ellipse cx="32" cy="96.6" rx="25" ry="2.3" fill="#031A20" opacity=".6" filter={url("blur")} />
    {/* Frame: two turned posts, a plate above and below the glass, a lid and a pedestal. */}
    <rect x="11.4" y="15" width="2.3" height="70" rx="1.15" fill={url("post")} />
    <rect x="50.3" y="15" width="2.3" height="70" rx="1.15" fill={url("post")} />
    {/* Sand sits inside the glass. */}
    <g clipPath={url("up")}>
      <path d={`M10 ${surface} H20 Q32 ${surface + dip * 2} 44 ${surface} H54 V51 H10 Z`} fill={url("sand")} />
      <rect x="10" y={surface} width="44" height={51 - surface} fill={url("shade")} />
      {e < 0.995 && <ellipse cx="32" cy={surface + dip * 0.9} rx={Math.max(3, 12 * Math.min(1, (NECK - surface) / 12))} ry="1.2" fill="#FBF0CF" opacity=".55" />}
    </g>
    <g clipPath={url("low")}>
      <path d={`M8 92 L8 ${shoulder} C23 ${shoulder} 28 ${apex + 0.8} 32 ${apex} C36 ${apex + 0.8} 41 ${shoulder} 56 ${shoulder} L56 92 Z`} fill={url("sand")} />
      <rect x="10" y={apex} width="44" height={92 - apex} fill={url("shade")} />
      {e > 0.02 && <ellipse cx="32" cy={apex + 0.5} rx={Math.min(3.6, 0.8 + (BOTTOM - apex) * 0.1)} ry="0.7" fill="#FBF0CF" opacity=".6" />}
    </g>
    {streaming && <line className="r8-sg-stream" x1="32" x2="32" y1={NECK - 3} y2={apex + 0.6} />}
    {flowing && streaming && [0, 1, 2].map(i => <circle key={i} className="r8-sg-grain" cx="32" cy={NECK} r=".75" style={{ "--fall": `${(apex - NECK).toFixed(1)}px`, animationDelay: `${i * 0.17}s` } as CSSProperties} />)}
    {/* Glass over the sand: body tint, a rim, the far wall seen through the near one, and the lit left edge. */}
    <path d={UPPER} fill={url("glass")} stroke={url("rim")} strokeWidth="1" />
    <path d={LOWER} fill={url("glass")} stroke={url("rim")} strokeWidth="1" />
    <path d="M44.4 22 C44.4 33 36.5 38 33 46" fill="none" stroke="#CDE8E6" strokeOpacity=".24" strokeWidth=".8" strokeLinecap="round" />
    <path d="M44.4 78 C44.4 67 38 62.5 34.6 55" fill="none" stroke="#CDE8E6" strokeOpacity=".2" strokeWidth=".8" strokeLinecap="round" />
    <path d="M20.2 23 C20.2 32 26.6 36.2 30 44" fill="none" stroke="#FFFFFF" strokeOpacity=".92" strokeWidth="1.7" strokeLinecap="round" />
    <path d="M20.2 77 C20.2 70 25 66.5 29 61" fill="none" stroke="#FFFFFF" strokeOpacity=".5" strokeWidth="1.3" strokeLinecap="round" />
    <path d="M24.5 18.4 H30" fill="none" stroke="#FFFFFF" strokeOpacity=".55" strokeWidth="1" strokeLinecap="round" />
    <circle cx="31.9" cy="49.6" r="1.1" fill="#FFFFFF" opacity=".55" />
    {burst > 0 && <path key={burst} className="r8-sg-flash" d="M20.2 23 C20.2 32 26.6 36.2 30 44" fill="none" stroke="#FFFFFF" strokeWidth="3.4" strokeLinecap="round" />}
    {/* Plates, lid and pedestal. */}
    <rect x="9.5" y="9" width="45" height="6.5" rx="2" fill={url("metal")} />
    <rect x="6.5" y="4.6" width="51" height="4.6" rx="2.2" fill={url("metal")} />
    <rect x="9.5" y="84.5" width="45" height="6.5" rx="2" fill={url("metal")} />
    <rect x="6.5" y="90.8" width="51" height="4.6" rx="2.2" fill={url("metal")} />
    <path d="M8.6 5.5 H55.4 M11 9.8 H53 M11 85.3 H53 M8.6 91.6 H55.4" stroke="#F4FBFA" strokeOpacity=".5" strokeWidth=".6" strokeLinecap="round" />
    <path d="M9.5 15.2 H54.5 M9.5 84.7 H54.5" stroke="#0A2A32" strokeOpacity=".4" strokeWidth=".8" />
  </svg>;
}
