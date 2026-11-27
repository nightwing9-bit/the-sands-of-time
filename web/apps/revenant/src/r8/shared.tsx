import { Component, useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { createApiClient, describeFailure, type ApiResult, type components } from "@sands/contracts";

export type S = components["schemas"];
export type Snapshot = S["R8Snapshot"];
export type Balance = S["R8Balance"];
export type Preview = S["R8Preview"];
export type Job = S["R8Job"];
export type Verified = S["R8Verified"];
export type Case = S["R8Case"];
export type Payment = S["R8Payment"];
export type Api = ReturnType<typeof createApiClient>;

export async function unwrap<T>(promise: Promise<ApiResult<T>>) {
  const result = await promise;
  if (!result.ok) throw new Error(describeFailure(result.failure) + ("code" in result.failure && result.failure.code ? " · " + result.failure.code : ""));
  return result.value;
}

export function useLoad<T>(read: (() => Promise<T>) | null) {
  const [value, setValue] = useState<T | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const serial = useRef(0);
  const reload = useCallback(async () => {
    if (!read) return;
    const seq = ++serial.current;
    setLoading(true);
    try { const result = await read(); if (serial.current === seq) { setValue(result); setError(""); } }
    catch (e) { if (serial.current === seq) setError(String(e instanceof Error ? e.message : e)); }
    finally { if (serial.current === seq) setLoading(false); }
  }, [read]);
  useEffect(() => { setValue(null); setError(""); void reload(); return () => { serial.current++; }; }, [reload]);
  return { value, error, loading, reload };
}

// The durable query remains authoritative; reuse the replay poll pattern for active finance views.
export function useFinanceRefresh(active: boolean, refresh: () => Promise<void>) {
  useEffect(() => {
    if (!active) return;
    let disposed = false;
    let timer: ReturnType<typeof setTimeout>;
    async function refreshVisible() {
      if (document.visibilityState === "visible") await refresh();
      if (!disposed) timer = setTimeout(() => void refreshVisible(), 2500);
    }
    timer = setTimeout(() => void refreshVisible(), 2500);
    return () => { disposed = true; clearTimeout(timer); };
  }, [active, refresh]);
}

/** One idempotency key per logical operation: reused on retry, cleared on success. */
export function useOperation() {
  const [busy, setBusy] = useState(false), [error, setError] = useState(""), [note, setNote] = useState("");
  const key = useRef<{ identity: string; value: string } | null>(null);
  const clear = useCallback(() => { setError(""); setNote(""); }, []);
  async function run(identity: string, work: (key: string) => Promise<string | void>) {
    if (busy) return false;
    setBusy(true); setError(""); setNote("");
    if (key.current?.identity !== identity) key.current = { identity, value: crypto.randomUUID() };
    try { const message = await work(key.current.value); key.current = null; if (message) setNote(message); return true; }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); return false; }
    finally { setBusy(false); }
  }
  return { busy, error, note, run, clear };
}

/** Pixel size of a container, so SVG charts draw at 1:1 and text is never stretched. */
export function useSize<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => setSize(s => {
      const width = Math.floor(el.clientWidth), height = Math.floor(el.clientHeight);
      return s.width === width && s.height === height ? s : { width, height };
    });
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  return [ref, size] as const;
}

export function ErrorText({ error }: { error: string }) { return error ? <p className="r8-error" role="alert">{error}</p> : null; }

export function Table({ headers, rows, className, caption }: { headers: ReactNode[]; rows: ReactNode[][]; className?: string; caption?: string }) {
  return <div className={"r8-scroll " + (className ?? "")} tabIndex={0}><table>{caption && <caption className="r8-sr">{caption}</caption>}
    <thead><tr>{headers.map((h, i) => <th key={i} scope="col">{h}</th>)}</tr></thead>
    <tbody>{rows.map((row, i) => <tr key={i}>{row.map((c, j) => <td key={j}>{c}</td>)}</tr>)}</tbody></table></div>;
}

export function Facts({ rows }: { rows: string[][] }) {
  return <dl className="r8-facts">{rows.map(([name, value]) => <div key={name}><dt>{name}</dt><dd><code>{value}</code></dd></div>)}</dl>;
}

/** "View as table" disclosure that opens as an overlay so it never pushes the primary composition. */
export function Drawer({ summary, children, className }: { summary: string; children: ReactNode; className?: string }) {
  return <details className={"r8-drawer " + (className ?? "")}><summary>{summary}</summary><div className="r8-drawer-body">{children}</div></details>;
}

const paths = {
  tower: "M3 21h18M6 17v4M10 13v8M14 9v12M18 4v17",
  held: "m12 3 9 5-9 5-9-5 9-5ZM3 13l9 5 9-5M3 18l9 5 9-5",
  liquidity: "m2 18 6-6 5 4 9-11M17 5h5v5",
  rewind: "M4 8v-5M4 3l-3 3m3-3 3 3M4 12a8 8 0 1 1 2.4 5.7M12 8v5l3 2",
  audit: "M12 2 20 5v7c0 5-3.4 8-8 10-4.6-2-8-5-8-10V5l8-3ZM8 12l3 3 5-6",
  arrow: "M4 12h16m-6-6 6 6-6 6",
  close: "M5 5l14 14M19 5 5 19",
  search: "m21 21-4.3-4.3M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14Z",
  lock: "M6 11h12v10H6zM8 11V7a4 4 0 0 1 8 0v4",
  shield: "M12 2 20 5v7c0 5-3.4 8-8 10-4.6-2-8-5-8-10V5l8-3ZM8 12l3 3 5-6",
  warn: "M12 3 2 21h20L12 3ZM12 10v5M12 18h.01",
  download: "M12 3v12m-5-5 5 5 5-5M4 21h16",
  fork: "M6 3v6a6 6 0 0 0 6 6h0a6 6 0 0 1 6 6M18 3v6",
  refresh: "M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7",
} as const;
export function Icon({ name, className }: { name: keyof typeof paths; className?: string }) {
  return <svg aria-hidden="true" className={"r8-icon " + (className ?? "")} fill="none" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.7" viewBox="0 0 24 24"><path d={paths[name]} /></svg>;
}

export function Hourglass({ className, level }: { className?: string; level?: number }) {
  // level: 0..1 share of the timeline already passed (top bulb drains into the bottom bulb).
  const l = level === undefined ? null : Math.min(1, Math.max(0, level));
  return <svg aria-hidden="true" className={className} fill="none" viewBox="0 0 32 40">
    {l !== null && <><path d={`M${11 + 5 * l} ${6 + 12 * l}h${10 - 10 * l}L16 19Z`} fill="currentColor" opacity=".35" />
      <path d={`M16 22 ${10 + 0} 35h12Z`} fill="currentColor" opacity={0.15 + 0.4 * l} /></>}
    <path d="M7 4h18M7 36h18M10 5c0 8 12 8 12 15S10 27 10 35M22 5c0 8-12 8-12 15s12 7 12 15" stroke="currentColor" strokeLinecap="round" strokeWidth="2" />
    <path d="m12 13 8 0M13 27h6" stroke="currentColor" strokeLinecap="round" strokeWidth="1.6" />
  </svg>;
}

export const isOpenCase = (c: Case) => c.status === "Open" || c.status === "AwaitingSecondApproval";

/** Keeps a rendering fault in one screen from blanking the authenticated shell; it never substitutes data. */
export class ScreenBoundary extends Component<{ children: ReactNode }, { error: string | null }> {
  state = { error: null as string | null };
  static getDerivedStateFromError(e: unknown) { return { error: e instanceof Error ? e.message : String(e) }; }
  render() {
    return this.state.error ? <p className="r8-error" role="alert">This screen could not render the returned data: {this.state.error}</p> : this.props.children;
  }
}
