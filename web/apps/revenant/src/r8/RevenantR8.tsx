/// <reference types="vite/client" />
import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { createApiClient, readCsrfToken, STAFF_CSRF_COOKIE } from "@sands/contracts";
import "./r8.css";
import { clock } from "./format.ts";
import { ErrorText, Hourglass, Icon, isOpenCase, ScreenBoundary, unwrap, useFinanceRefresh, useLoad, useOperation } from "./shared.tsx";
import { SignIn } from "./SignIn.tsx";
import { Tower } from "./Tower.tsx";
import { Held } from "./Held.tsx";
import { Liquidity } from "./Liquidity.tsx";
import { Rewind } from "./Rewind.tsx";
import { Audit } from "./Audit.tsx";

export { money } from "./format.ts";

// Visible tab text follows the approved design; accessible names keep the established screen titles.
const routes = [["tower", "Control Tower", "Control Tower", "tower"], ["held", "Held Queue", "Held Queue", "held"],
  ["liquidity", "Liquidity Fan", "Liquidity Fan", "liquidity"], ["rewind", "Rewind Mode", "Rewind", "rewind"], ["audit", "Audit", "Audit", "audit"]] as const;
type Route = typeof routes[number][0];
const title = (route: Route) => routes.find(([key]) => key === route)![1];
const currentRoute = (): Route => routes.find(([key]) => location.hash === "#" + key)?.[0] ?? "tower";
const initialSelection = new URLSearchParams(location.search);
function selectInUrl(run: string | null, job: string | null, fork: string | null = null) {
  const url = new URL(location.href);
  for (const [k, v] of [["run", run], ["job", job], ["fork", fork]] as const) if (v) url.searchParams.set(k, v); else url.searchParams.delete(k);
  history.replaceState(null, "", url);
}
const runPath = "/revenant/r8/runs/{id}" as const;

export function RevenantR8() {
  const [token, setToken] = useState<string | null>(() => import.meta.env.DEV
    ? (import.meta.env.R8_PREVIEW_TOKEN as string | undefined) ?? null : null);
  const [route, setRoute] = useState(currentRoute);
  const [id, setId] = useState<string | null>(() => initialSelection.get("run")), [seed, setSeed] = useState("42");
  const [jobId, setJobId] = useState<string | null>(() => initialSelection.get("job")), [focusIntent, setFocusIntent] = useState<string | null>(null);
  const [forkPosition, setForkPosition] = useState<string | null>(() => initialSelection.get("fork"));
  const [login, setLogin] = useState(""), [password, setPassword] = useState(""), [totp, setTotp] = useState("");
  const create = useOperation(), signIn = useOperation();
  const api = useMemo(() => createApiClient({ baseUrl: "", getAccessToken: () => token, getCsrfToken: () => readCsrfToken(document.cookie, [STAFF_CSRF_COOKIE]), timeoutMs: 45_000 }), [token]);
  const refreshing = useRef(false);
  useEffect(() => {
    if (refreshing.current || token || !readCsrfToken(document.cookie, [STAFF_CSRF_COOKIE])) return;
    refreshing.current = true;
    void api.post("/auth/staff/refresh").then(r => { if (r.ok && r.value?.accessToken) setToken(r.value.accessToken); });
  }, [api, token]);
  const list = useLoad(useMemo(() => token ? () => unwrap(api.get("/revenant/r8/runs")) : null, [api, token]));
  const me = useLoad(useMemo(() => token ? () => unwrap(api.get("/auth/staff/me")) : null, [api, token]));
  const snap = useLoad(useMemo(() => id && token ? () => unwrap(api.get(runPath, { path: { id } })) : null, [api, id, token]));
  const live = Boolean(token && id && snap.value?.status !== "Verified");
  useFinanceRefresh(live && (route === "tower" || route === "held" || route === "liquidity"), snap.reload);
  const job = useLoad(useMemo(() => token && id && jobId ? () => unwrap(api.get("/revenant/r8/runs/{id}/counterfactual/{job}", { path: { id, job: jobId } })) : null, [api, id, jobId, token]));
  useEffect(() => { const onHash = () => setRoute(currentRoute()); addEventListener("hashchange", onHash); return () => removeEventListener("hashchange", onHash); }, []);
  useEffect(() => {
    if (!token || !jobId || job.value?.status === "Verified" || job.value?.status === "Failed" || job.value?.status === "Cancelled") return;
    const timer = setInterval(() => void job.reload(), 1200);
    return () => clearInterval(timer);
  }, [token, jobId, job.value?.status, job.reload]);

  function select(run: string | null) { setId(run); setJobId(null); setForkPosition(null); setFocusIntent(null); selectInUrl(run, null); }
  function chooseJob(next: string, fork: string) {
    setJobId(next); setForkPosition(fork); selectInUrl(id, next, fork);
    if (next === jobId) void job.reload();
  }
  function doLogin(e: FormEvent) {
    e.preventDefault();
    void signIn.run(login, async () => {
      const result = await unwrap(api.post("/auth/staff/login", { body: { userName: login, password, totpCode: totp || null } }));
      if (!result?.accessToken) throw new Error("The session did not supply an access token.");
      setToken(result.accessToken); setPassword(""); setTotp("");
    });
  }
  const state = snap.value && id && snap.value.runId === id ? snap.value : null;
  const heldCount = state?.cases.filter(isOpenCase).length ?? 0;
  const screeningPolicy = state?.payments.find(p => p.risk)?.risk?.policy ?? state?.cases.find(c => c.risk)?.risk?.policy ?? null;
  const roles = me.value?.roles ?? [];
  const role = roles.find(r => r === "supervisor") ?? roles.find(r => r === "operator") ?? roles[0] ?? null;
  const verifiedJob = job.value?.jobId === jobId ? job.value : null;

  if (!token) return <main className="r8" data-testid="revenant-r8">
    <SignIn login={login} password={password} totp={totp} setLogin={setLogin} setPassword={setPassword} setTotp={setTotp}
      onSubmit={doLogin} busy={signIn.busy} error={signIn.error} />
  </main>;

  return <main className="r8" data-testid="revenant-r8">
    <header className="r8-mast">
      <div className="r8-brand"><Hourglass className="r8-brand-mark" /><span>Revenant</span></div>
      <nav className="r8-nav" aria-label="Revenant screens">{routes.map(([r, name, visible, icon]) =>
        <a key={r} href={"#" + r} aria-current={route === r ? "page" : undefined}>
          <Icon name={icon} /><span>{visible}{visible !== name && <span className="r8-sr">{name.slice(visible.length)}</span>}</span>
          {r === "held" && <b className="r8-badge" title={`${heldCount} open review cases in this run`}>{heldCount}</b>}
        </a>)}</nav>
      <div className="r8-mast-right">
        <span className="r8-chip is-synthetic"><i />Synthetic data</span>
        <time className="r8-clock" title="Scenario clock: last sealed boundary of the selected run, not wall time">{state ? clock(state.boundaryMs) : "--:--:--"} <span>UTC</span></time>
        {role && <span className="r8-role" title={me.value?.subject}>{role[0]!.toUpperCase() + role.slice(1)}</span>}
        <button className="r8-btn is-quiet" onClick={() => { void api.post("/auth/staff/logout").finally(() => { setToken(null); select(null); }); }}>Sign out</button>
      </div>
    </header>
    <div className="r8-statusline">
      <span>Synthetic data — no real funds</span><i />
      <span><b className={"r8-dot" + (live ? " is-live" : "")} />{state ? (live ? "Refresh · polling every 2.5 s" : "Static · source closed and verified") : "No run selected"}</span><i />
      <span>Screening policy <strong>{screeningPolicy ?? "—"}</strong></span><i />
      <span>{state ? <>Boundary {clock(state.boundaryMs)} UTC · {state.status}</> : "Scenario clock idle"}</span>
      <div className="r8-runbar">
        <label><span className="r8-sr">Scenario</span><select aria-label="Scenario" value={id ?? ""} onChange={e => select(e.target.value || null)}><option value="">Select a run</option>
          {id && !list.value?.some(r => r.runId === id) && <option value={id}>{id.slice(0, 8)}</option>}
          {list.value?.map(r => <option key={r.runId} value={r.runId}>Seed {r.seed} · {r.runId.slice(0, 8)}</option>)}</select></label>
        <form onSubmit={e => { e.preventDefault(); void create.run(seed, async key => {
          const run = await unwrap(api.post("/revenant/r8/runs", { body: { seed }, idempotencyKey: key }));
          select(run.runId); void list.reload();
          if (run.runId === id) void snap.reload();
          return "Scenario ready";
        }); }}>
          <label><span className="r8-sr">Seed</span><input aria-label="Seed" required pattern="-?[0-9]+" inputMode="numeric" value={seed} onChange={e => setSeed(e.target.value)} /></label>
          <button className="r8-btn is-primary is-small" disabled={create.busy}>{create.busy ? "Opening scenario…" : "Launch fresh scenario"}</button>
        </form>
      </div>
    </div>
    <div className="r8-work">
      {(create.error || list.error || snap.error || create.note) && <div className="r8-toast">
        <ErrorText error={create.error || list.error || snap.error} />{create.note && <p role="status">{create.note}</p>}</div>}
      {!id && !create.busy && <div className="r8-empty-state"><Hourglass className="r8-empty-mark" />
        <div className="r8-eyebrow">NO SCENARIO SELECTED</div><h1>Launch a fresh seeded scenario or select an existing run.</h1>
        <p>Each run is a synthetic six-bank settlement session. Use the seed and Launch fresh scenario controls in the status bar above.</p></div>}
      {(create.busy || (id && !snap.value && !snap.error)) && <p className="r8-loading" role="status">{create.busy ? "Opening scenario…" : "Loading committed financial state…"}</p>}
      {state && id && <section className="r8-screen" key={id + route} aria-label={title(route)} data-screen={route}><ScreenBoundary>
        {route === "tower" && <Tower api={api} state={state} live={live} openCase={intent => { setFocusIntent(intent); location.hash = "held"; }} />}
        {route === "held" && <Held state={state} api={api} reload={snap.reload} focus={focusIntent} role={role} />}
        {route === "liquidity" && <Liquidity id={id} api={api} state={state} live={live} />}
        {route === "rewind" && <Rewind api={api} state={state} job={verifiedJob} jobError={job.error} reloadJob={job.reload}
          chooseJob={chooseJob} reload={snap.reload} forkPosition={forkPosition} />}
        {route === "audit" && <Audit api={api} id={id} result={verifiedJob?.status === "Verified" ? verifiedJob.verified : null} />}
      </ScreenBoundary></section>}
    </div>
  </main>;
}
