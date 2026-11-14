import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { createApiClient, readCsrfToken, STAFF_CSRF_COOKIE, describeFailure, isTimelinePosition, type ApiResult, type components, type TimelinePosition } from "@sands/contracts";
import { TimelineScrubber, type TimelineAdapter } from "@sands/timeline-ui";
import "./r8.css";
import { bufferDisclosure, bufferLabel, bufferPolicy, bufferReference, type BufferReference } from "./bufferReference.ts";

type S = components["schemas"];
type Snapshot = S["R8Snapshot"];
type Balance = S["R8Balance"];
type Preview = S["R8Preview"];
type Job = S["R8Job"];
type Verified = S["R8Verified"];
type Api = ReturnType<typeof createApiClient>;
const routes = [["tower","Control Tower"],["held","Held Queue"],["liquidity","Liquidity Fan"],["rewind","Rewind Mode"],["audit","Audit"]] as const;
type Route = typeof routes[number][0];
const title = (route: Route) => routes.find(([key]) => key === route)![1];
const currentRoute = (): Route => routes.find(([key]) => location.hash === "#" + key)?.[0] ?? "tower";
const initialSelection = new URLSearchParams(location.search);
function selectInUrl(run: string | null, job: string | null) {
  const url = new URL(location.href);
  if (run) url.searchParams.set("run",run); else url.searchParams.delete("run");
  if (job) url.searchParams.set("job",job); else url.searchParams.delete("job");
  history.replaceState(null,"",url);
}
const runPath = "/revenant/r8/runs/{id}" as const;
const time = (ms: number) => new Intl.DateTimeFormat("en-GB",{timeZone:"UTC",hour:"2-digit",minute:"2-digit",hourCycle:"h23"}).format(ms) + " UTC";
const group = new Intl.NumberFormat("en-US");
export function money(value: string, signed = false) {
  const n = BigInt(value);
  const x = n < 0n ? -n : n;
  return (n < 0n ? "−" : signed && n > 0n ? "+" : "") + "$" + group.format(x / 100n) + "." + String(x % 100n).padStart(2,"0");
}
const signedInteger = (s: string) => BigInt(s) > 0n ? "+" + s : s.replace("-", "−");
async function unwrap<T>(promise: Promise<ApiResult<T>>) {
  const result = await promise;
  if (!result.ok) throw new Error(describeFailure(result.failure) + ("code" in result.failure && result.failure.code ? " · " + result.failure.code : ""));
  return result.value;
}
function useLoad<T>(read: (() => Promise<T>) | null) {
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
function useFinanceRefresh(active: boolean, refresh: () => Promise<void>) {
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
function ErrorText({ error }: {error: string}) { return error ? <p className="r8-error" role="alert">{error}</p> : null; }
function useOperation() {
  const [busy, setBusy] = useState(false), [error, setError] = useState(""), [note, setNote] = useState("");
  const key = useRef<{ identity: string; value: string } | null>(null);
  async function run(identity: string, work: (key: string) => Promise<string | void>) {
    if (busy) return;
    setBusy(true); setError(""); setNote("");
    if (key.current?.identity !== identity) key.current = { identity, value: crypto.randomUUID() };
    try { const message = await work(key.current.value); key.current = null; if (message) setNote(message); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  }
  return { busy, error, note, run };
}

export function RevenantR8() {
  const [token,setToken] = useState<string | null>(null), [route,setRoute] = useState(currentRoute);
  const [id,setId] = useState<string | null>(()=>initialSelection.get("run")), [seed,setSeed] = useState("42");
  const [jobId,setJobId] = useState<string | null>(()=>initialSelection.get("job")), [focusIntent,setFocusIntent] = useState<string | null>(null);
  const [login,setLogin] = useState(""), [password,setPassword] = useState(""), [totp,setTotp] = useState("");
  const create = useOperation(), signIn = useOperation();
  const api = useMemo(() => createApiClient({baseUrl:"", getAccessToken:()=>token, getCsrfToken:()=>readCsrfToken(document.cookie, [STAFF_CSRF_COOKIE]), timeoutMs:45_000}),[token]);
  const refreshing = useRef(false);
  useEffect(() => {
    if (refreshing.current || token || !readCsrfToken(document.cookie, [STAFF_CSRF_COOKIE])) return;
    refreshing.current = true;
    void api.post("/auth/staff/refresh").then(r => { if (r.ok && r.value?.accessToken) setToken(r.value.accessToken); });
  }, [api, token]);
  const list = useLoad(useMemo(() => token ? () => unwrap(api.get("/revenant/r8/runs")) : null,[api,token]));
  const snap = useLoad(useMemo(() => id && token ? () => unwrap(api.get(runPath,{path:{id}})) : null,[api,id,token]));
  const live = Boolean(token && id && snap.value?.status !== "Verified");
  useFinanceRefresh(live && (route === "tower" || route === "held" || route === "liquidity"), snap.reload);
  const job = useLoad(useMemo(() => token && id && jobId ? () => unwrap(api.get("/revenant/r8/runs/{id}/counterfactual/{job}",{path:{id,job:jobId}})) : null,[api,id,jobId,token]));
  useEffect(() => { const onHash = () => setRoute(currentRoute()); addEventListener("hashchange",onHash); return () => removeEventListener("hashchange",onHash); },[]);
  useEffect(() => {
    if (!token || !jobId || job.value?.status === "Verified" || job.value?.status === "Failed" || job.value?.status === "Cancelled") return;
    const timer = setInterval(() => void job.reload(), 1200);
    return () => clearInterval(timer);
  },[token,jobId,job.value?.status,job.reload]);

  function select(run: string | null) { setId(run); setJobId(null); setFocusIntent(null); selectInUrl(run,null); }
  function chooseJob(job: string) { setJobId(job); selectInUrl(id,job); }
  function doLogin(e: FormEvent) {
    e.preventDefault();
    void signIn.run(login, async () => {
      const result = await unwrap(api.post("/auth/staff/login",{body:{userName:login,password,totpCode:totp || null}}));
      if (!result?.accessToken) throw new Error("The session did not supply an access token.");
      setToken(result.accessToken); setPassword(""); setTotp("");
    });
  }
  return <main className="r8" data-testid="revenant-r8">
    <header className="r8-header"><div><div className="r8-eyebrow">MARROW FINANCIAL OBSERVATORY</div><div className="r8-brand">Revenant<span> R8</span></div></div>
      <span className="r8-tag">Synthetic finance · USD</span></header>
    {!token ? <section className="r8-signin">
      <div className="r8-eyebrow">STAFF ACCESS</div><h1>The ledger remembers.</h1>
      <p>Sign in to operate a six-bank scenario, review held payments and compare two verified financial histories.</p>
      <form onSubmit={doLogin}><label>Username<input autoComplete="username" required value={login} onChange={e=>setLogin(e.target.value)}/></label>
        <label>Password<input autoComplete="current-password" type="password" required value={password} onChange={e=>setPassword(e.target.value)}/></label>
        <label>MFA code<input inputMode="numeric" autoComplete="one-time-code" value={totp} onChange={e=>setTotp(e.target.value)}/></label>
        <button className="r8-primary" disabled={signIn.busy}>Sign in</button></form><ErrorText error={signIn.error}/>
    </section> : <>
      <nav className="r8-nav" aria-label="Revenant screens">{routes.map(([r,label],i)=><a key={r} href={"#"+r} aria-current={route===r?"page":undefined}><small>{String(i+1).padStart(2,"0")}</small>{label}</a>)}</nav>
      <div className="r8-toolbar">
        <label>Scenario <select aria-label="Scenario" value={id??""} onChange={e=>select(e.target.value || null)}><option value="">Select a run</option>
          {id && !list.value?.some(r=>r.runId===id) && <option value={id}>{id.slice(0,8)}</option>}
          {list.value?.map(r=><option key={r.runId} value={r.runId}>Seed {r.seed} · {r.runId.slice(0,8)}</option>)}</select></label>
        <form onSubmit={e=>{e.preventDefault();void create.run(seed,async key=>{
          const run = await unwrap(api.post("/revenant/r8/runs",{body:{seed},idempotencyKey:key}));
          select(run.runId); void list.reload(); return "Scenario ready";
        });}}>
          <label>Seed <input aria-label="Seed" required pattern="-?[0-9]+" inputMode="numeric" value={seed} onChange={e=>setSeed(e.target.value)}/></label>
          <button className="r8-primary" disabled={create.busy}>{create.busy?"Opening scenario…":"Launch fresh scenario"}</button>
        </form>
        <button onClick={()=>{void api.post("/auth/staff/logout").finally(()=>{setToken(null);select(null);});}}>Sign out</button>
      </div>
      <ErrorText error={create.error || list.error || snap.error}/>
      {create.note && <p role="status">{create.note}</p>}
      <div className="r8-heading"><div className="r8-eyebrow">REVENANT / {String(routes.findIndex(x=>x[0]===route)+1).padStart(2,"0")}</div>
        <h1>{title(route)}</h1>{snap.value && <span className="r8-tag">{snap.value.status} · {time(snap.value.boundaryMs)} · {snap.value.runId.slice(0,8)}</span>}</div>
      {!id && !create.busy && <p className="r8-empty">Launch a fresh seeded scenario or select an existing run.</p>}
      {id && !snap.value && !snap.error && <p role="status">Loading committed financial state…</p>}
      {snap.value && id && snap.value.runId === id && <section key={id+route} aria-label={title(route)}>
        {route === "tower" && <Tower state={snap.value} openCase={intent=>{setFocusIntent(intent);location.hash="held";}}/>}
        {route === "held" && <Held state={snap.value} api={api} reload={snap.reload} focus={focusIntent}/>}
        {route === "liquidity" && <Liquidity id={id} api={api} opening={snap.value.opening} live={live}/>}
        {route === "rewind" && <Rewind api={api} state={snap.value} job={job.value?.jobId === jobId ? job.value : null} jobError={job.error} reloadJob={job.reload}
          chooseJob={chooseJob} reload={snap.reload}/>}
        {route === "audit" && <Audit api={api} id={id} result={job.value?.jobId === jobId && job.value.status === "Verified" ? job.value.verified : null}/>}
      </section>}
      <footer className="r8-footer">MARROW × REVENANT <span>Scenario clocks · committed USD minor units</span></footer>
    </>}
  </main>;
}

function Panel({name,children}:{name:string;children:ReactNode}) {return <section className="r8-panel"><h2>{name}</h2>{children}</section>;}
function Balances({balances}:{balances:Balance[]}) {
  return <Table headers={["Bank","Posted","Reserved","Available"]} rows={balances.map(b=>[b.bankId,money(b.postedMinor),money(b.reservedMinor),money(b.availableMinor)])}/>;
}
function Table({headers,rows}:{headers:string[];rows:ReactNode[][]}) {
  return <div className="r8-scroll" tabIndex={0}><table><thead><tr>{headers.map(h=><th key={h} scope="col">{h}</th>)}</tr></thead>
    <tbody>{rows.map((row,i)=><tr key={i}>{row.map((c,j)=><td key={j}>{c}</td>)}</tr>)}</tbody></table></div>;
}
function Tower({state,openCase}:{state:Snapshot;openCase:(id:string)=>void}) {
  const anchors = state.balances.map((b,i)=>({b,x:[116,293,474,467,292,111][i]!,y:[92,64,99,228,261,228][i]!}));
  const graphId = "r8-flow-arrow";
  const held = state.payments.filter(p=>p.status==="Held").length;
  const settled = state.payments.filter(p=>p.status==="Settled").length;
  return <>
    <div className="r8-metrics"><div><small>SETTLED PAYMENTS</small><strong>{settled}</strong></div><div><small>HELD PAYMENTS</small><strong>{held}</strong></div>
      <div><small>RESERVED ACROSS BANKS</small><strong>{money(state.balances.reduce((sum,b)=>sum+BigInt(b.reservedMinor),0n).toString())}</strong></div></div>
    <div className="r8-split"><Panel name="Six-bank constellation">
      <svg className="r8-network" viewBox="0 0 590 330" role="img" aria-label="Committed payment flows among the six banks">
        <defs><pattern id="r8-grid" width="28" height="28" patternUnits="userSpaceOnUse"><path d="M 28 0 L 0 0 0 28" fill="none" stroke="#283035" strokeWidth="0.8"/></pattern>
          <marker id={graphId} markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0 0 L6 3 L0 6" fill="none" stroke="context-stroke"/></marker></defs>
        <rect width="590" height="330" fill="url(#r8-grid)"/>
        {state.payments.filter(p=>["Settled","Held","Returned"].includes(p.status)).map(p=>{
          const a=anchors.find(k=>k.b.bankId===p.sourceBank),b=anchors.find(k=>k.b.bankId===p.destinationBank); if(!a||!b||a===b)return null;
          const dx=b.x-a.x, dy=b.y-a.y, length=Math.hypot(dx,dy), ex=b.x-dx/length*19, ey=b.y-dy/length*19;
          return <path key={p.intentId} d={`M ${a.x} ${a.y} Q ${(a.x+b.x)/2-dy/4} ${(a.y+b.y)/2+dx/4} ${ex} ${ey}`}
            fill="none" stroke={p.status==="Held"?"#eab97a":"#91e7ba"} strokeWidth="2" strokeDasharray={p.status==="Held"?"5 6":undefined} markerEnd={`url(#${graphId})`}><title>{p.intentId} · {p.status} · {money(p.amountMinor)}</title></path>;
        })}
        {anchors.map(({b,x,y})=><g key={b.bankId}><circle cx={x} cy={y} r="12" fill="#171f21" stroke="#91e7ba" strokeWidth="2"/>
          <circle cx={x} cy={y} r="4" fill="#91e7ba"/><text x={x} y={y+34} textAnchor="middle">{b.bankId}</text></g>)}
      </svg><p className="r8-muted">Solid: settled flow · dashed: held reservation</p>
    </Panel><Panel name="Current balances"><Balances balances={state.balances}/><details><summary>Opening balances</summary><Balances balances={state.opening}/></details></Panel></div>
    <Panel name="Recorded velocity alerts"><p className="r8-muted">Advisory only · {state.anomalyStatus}</p>
      {state.anomalies.length > 0 && <Table headers={["Account","As of","Observed","Expected","Score","Threshold","Evidence"]} rows={state.anomalies.map(x=>[
        x.subject,time(x.atMs),x.observed,x.expected.toFixed(2),x.score.toFixed(3),x.threshold.toFixed(3),<code>{x.evidenceSha256}</code>])}/>}
    </Panel>
    <Panel name="Recent decisions"><Table headers={["Intent","Route","Principal","State","Screening"]} rows={state.payments.map(p=>[
      state.cases.some(c=>c.intentId===p.intentId) ? <button className="r8-text" onClick={()=>openCase(p.intentId)}>{p.intentId}</button> : p.intentId,
      p.sourceBank+" → "+p.destinationBank,money(p.amountMinor),p.status,p.risk ? `${p.risk.reason} · ${p.risk.scorePpm??"warmup"}` : p.reason])}/></Panel>
  </>;
}

function Held({state,api,reload,focus}:{state:Snapshot;api:Api;reload:()=>Promise<void>;focus:string|null}) {
  const op=useOperation();
  const [selected,setSelected]=useState(focus??state.cases[0]?.intentId??"");
  const c=state.cases.find(x=>x.intentId===selected);
  const open=c && ["Open","AwaitingSecondApproval"].includes(c.status);
  return <div className="r8-split"><Panel name="Review cases">{state.cases.length===0 ? <p>No cases have opened.</p> :
    <Table headers={["Intent","Status","Approvals"]} rows={state.cases.map(x=>[<button className="r8-text" onClick={()=>setSelected(x.intentId)}>{x.intentId}</button>,
      x.status,`${x.approvalCount}/${x.requiredApprovals}`])}/>}</Panel>
    <Panel name="Case evidence">{c ? <>
      <div className="r8-eyebrow">{c.intentId} · VERSION {c.version}</div><p className="r8-big">{money(c.amountMinor)}</p><p>{c.status} · {c.approvalCount} of {c.requiredApprovals} approvals</p>
      {c.requiredApprovals>1 && <p className="r8-muted">Release requires a second approval from a different supervisor.</p>}
      {c.risk && <><p>Recorded score: <strong>{c.risk.scorePpm??"Warmup"}</strong> ppm · pinned threshold {c.risk.thresholdPpm} ppm</p>
        <p>{c.risk.reason} · {c.risk.policy}</p>
        <Facts rows={c.risk.observations.map(x=>[x.name,x.value])}/><details><summary>Model and evidence</summary><code>{c.risk.modelId}</code><code>{c.risk.evidenceSha256}</code></details></>}
      <div className="r8-actions">{["Approve","Reject"].map(action=><button key={action} disabled={!open||op.busy} onClick={()=>void op.run(action+c.intentId+c.version,async key=>{
        try {const receipt=await unwrap(api.post("/revenant/r8/runs/{id}/cases/{intent}",{path:{id:state.runId,intent:c.intentId},
          body:{action,expectedVersion:c.version},idempotencyKey:key})); return "Committed: "+receipt.status;}
        finally {await reload();}
      })}>{action}</button>)}</div>
      <ErrorText error={op.error}/>{op.note&&<p role="status">{op.note}</p>}
    </> : <p>Select a case to inspect its recorded evidence.</p>}</Panel></div>;
}

function Chart({label, series, reference}: {
  label: string;
  series: {name: string; color: string; values: {at: number; minor: string}[]; dash?: boolean}[];
  reference?: BufferReference;
}) {
  const samples=series.flatMap(s=>s.values);
  if(samples.length===0)return <><p>No completed points.</p><BufferNote reference={reference}/></>;
  const lo=samples.reduce((v,x)=>BigInt(x.minor)<v?BigInt(x.minor):v,BigInt(samples[0]!.minor));
  const hi=samples.reduce((v,x)=>BigInt(x.minor)>v?BigInt(x.minor):v,lo);
  const start=Math.min(...samples.map(x=>x.at)), end=Math.max(...samples.map(x=>x.at));
  const referenceMinor = reference?.status === "Available" ? BigInt(reference.minor) : null;
  const y = (minor: bigint) => hi === lo ? 114 : 206 - Number((minor - lo) * 1840n / (hi - lo)) / 10;
  const placement = referenceMinor === null ? undefined : referenceMinor < lo ? "below" : referenceMinor > hi ? "above" : "in";
  return <figure className="r8-chart"><svg viewBox="0 0 680 248" role="img" aria-label={label}>
    {[22,68,114,160,206].map(y=><line key={y} x1="26" x2="656" y1={y} y2={y} stroke="#364045"/>)}
    {placement === "in" && referenceMinor !== null && <g aria-hidden="true" data-testid="r8-buffer-line">
      <line x1="26" x2="656" y1={y(referenceMinor)} y2={y(referenceMinor)} stroke="currentColor" strokeDasharray="3 5"/>
      <text x="650" y={y(referenceMinor) - 6} textAnchor="end">Operating buffer · {money(referenceMinor.toString())}</text>
    </g>}
    {series.map(s=><polyline key={s.name} fill="none" stroke={s.color} strokeWidth="2.5" strokeDasharray={s.dash?"6 4":undefined}
      points={s.values.map(x=>`${26+630*(x.at-start)/Math.max(1,end-start)},${y(BigInt(x.minor))}`).join(" ")}/>)}
    <text x="26" y="233">{time(start)}</text><text x="656" y="233" textAnchor="end">{time(end)}</text></svg>
    <figcaption>{series.map(s=><span key={s.name}><i style={{background:s.color}}/>{s.name}</span>)}</figcaption><BufferNote reference={reference} placement={placement}/></figure>;
}

function BufferNote({reference, placement}: {reference?: BufferReference; placement?: "below" | "above" | "in"}) {
  if (!reference) return null;
  return <div role="status" data-reference-status={reference.status}>
    <strong>{bufferLabel}</strong>
    {reference.status === "Available"
      ? <p>{money(reference.minor)}{placement === "above" || placement === "below" ? ` · ${placement} visible chart range` : ""}</p>
      : <p>Reference unavailable: a verified positive opening available balance is required.</p>}
    <p className="r8-muted">{bufferDisclosure} <code>{bufferPolicy}</code></p>
  </div>;
}

function Liquidity({id, api, opening, live}: {id: string; api: Api; opening: Balance[]; live: boolean}) {
  const data=useLoad(useMemo(()=>()=>unwrap(api.get("/revenant/r8/runs/{id}/liquidity",{path:{id}})),[api,id]));
  useFinanceRefresh(live, data.reload);
  const [bank,setBank]=useState("BANK-A");
  const reference = bufferReference(opening, bank);
  const actual=data.value?.actual.filter(x=>x.balance.bankId===bank)??[];
  const future=data.value?.projection.filter(x=>x.bankId===bank)??[];
  return <Panel name="Bank liquidity"><ErrorText error={data.error}/>{!data.value&&!data.error&&<p role="status">Loading completed bank observations…</p>}
    {data.value&&<><BankSelect value={bank} onChange={setBank}/><Chart reference={reference} label={bank+" posted, reserved, available and projected available liquidity"} series={[
      {name:"Posted",color:"#e5e6d9",values:actual.map(x=>({at:x.atMs,minor:x.balance.postedMinor}))},
      {name:"Available",color:"#91e7ba",values:actual.map(x=>({at:x.atMs,minor:x.balance.availableMinor}))},
      {name:"Reserved",color:"#eab97a",values:actual.map(x=>({at:x.atMs,minor:x.balance.reservedMinor}))},
      {name:"Statistical baseline",color:"#86bcf6",dash:true,values:future.map(x=>({at:x.atMs,minor:x.availableMinor}))},
    ]}/><p className="r8-muted">{data.value.method} · statistical baseline, {data.value.uncertainty} · anomaly: {data.value.anomalyStatus}</p>
      <details><summary>Liquidity data table</summary><p>Operating buffer reference: {reference.status === "Available" ? money(reference.minor) : "Unavailable"}</p><Table headers={["Time","Posted","Reserved","Available"]} rows={actual.map(x=>[
        time(x.atMs),money(x.balance.postedMinor),money(x.balance.reservedMinor),money(x.balance.availableMinor)])}/>
        <Table headers={["Projection time","Available baseline"]} rows={future.map(x=>[time(x.atMs),money(x.availableMinor)])}/></details></>}
    </Panel>;
}
function BankSelect({value,onChange}:{value:string;onChange:(s:string)=>void}) {
  return <label>Bank <select aria-label="Bank" value={value} onChange={e=>onChange(e.target.value)}>{["A","B","C","D","E","F"].map(b=><option key={b}>BANK-{b}</option>)}</select></label>;
}

function Rewind({api,state,job,jobError,reloadJob,chooseJob,reload}:{api:Api;state:Snapshot;job:Job|null;jobError:string;
  reloadJob:()=>Promise<void>;chooseJob:(s:string)=>void;reload:()=>Promise<void>}) {
  const op=useOperation();
  const [position,setPosition]=useState<TimelinePosition>("0");
  const [preview,setPreview]=useState<Preview|null>(null);
  const [policy,setPolicy]=useState("policy-v8"), [mode,setMode]=useState("policy"), [intent,setIntent]=useState(""),
    [action,setAction]=useState("Reject"), [version,setVersion]=useState(1);
  const seals=useMemo(()=>state.markers.filter(x=>x.kind==="ObservationSeal"),[state.markers]);
  const eligible=seals.find(x=>x.position===position && x.atMs<state.boundaryMs);
  const pending=job?.status==="Pending"||job?.status==="Running";
  const adapter=useMemo<TimelineAdapter<Preview>>(()=>({
    sourceId:state.timelineId,bounds:{start:"0",end:state.lastPosition},readOnly:true,
    getLabel:p=>{const mark=state.markers.find(x=>x.position===p);return mark ? `Event ${p} · ${time(mark.atMs)} · ${mark.kind}` : `Event ${p}`;},
    getMarkers:()=>seals.filter((x,i)=>i>=seals.length-4 || x.atMs===state.payments[0]?.acceptedAtMs)
      .map(m=>({position:m.position,label:time(m.atMs)+" completed seal"})),
    seek:async(p,o)=>{if(!isTimelinePosition(p))throw new Error("Invalid event position.");
      const value=await unwrap(api.get("/revenant/r8/runs/{id}/timeline/{position}",{path:{id:state.runId,position:p},signal:o?.signal}));
      return {position:value.position,state:value};},
    fork:async()=>{throw new Error("Submit a sandbox replay from a completed seal.");},
  }),[api,state.timelineId,state.runId,state.lastPosition,state.markers,state.payments,seals]);
  const payload:S["R8Replay"]={forkPosition:position,horizonMs:state.boundaryMs,policy,
    interventions:mode==="policy"?null:eligible?[{intentId:intent,action,expectedVersion:version,atMs:Math.max(eligible.atMs,state.payments.find(p=>p.intentId===intent)?.acceptedAtMs??eligible.atMs)}]:null};
  return <>
    <Panel name="Source timeline"><p className="r8-muted">Choose a completed seal before the comparison horizon. The sandbox inherits that exact verified prefix.</p>
      <TimelineScrubber adapter={adapter} position={position} onVerified={v=>{setPosition(v.position);setPreview(v.state);}}/>
      {preview&&<p data-testid="r8-preview">Verified event {preview.position} · {time(preview.boundaryMs)} · <code>{preview.stateHash.slice(0,16)}…</code></p>}
      {state.status!=="Verified" ? <button className="r8-primary" disabled={op.busy} onClick={()=>void op.run("close",async key=>{
        await unwrap(api.post("/revenant/r8/runs/{id}/close",{path:{id:state.runId},idempotencyKey:key}));await reload();return "Source verified";
      })}>Close and verify source</button> :
      <div className="r8-actions"><label>Alternative policy <select value={policy} onChange={e=>setPolicy(e.target.value)}><option value="policy-v8">V8 rules + pinned R5</option></select></label>
        <label>Comparison mode <select value={mode} onChange={e=>setMode(e.target.value)}><option value="policy">Policy only</option><option value="intervention">Policy + sandbox intervention</option></select></label>
        {mode==="intervention"&&<><label>Payment <select value={intent} onChange={e=>setIntent(e.target.value)}><option value="">Choose payment</option>{state.payments.map(p=><option key={p.intentId}>{p.intentId}</option>)}</select></label>
          <label>Analyst action <select value={action} onChange={e=>setAction(e.target.value)}><option>Reject</option><option>Approve</option></select></label>
          <label>Expected case version <input type="number" min="1" value={version} onChange={e=>setVersion(Number(e.target.value))}/></label></>}
        <button className="r8-primary" disabled={!eligible||!preview||pending||op.busy||(mode!=="policy"&&(!intent||!Number.isInteger(version)||version<1))}
          onClick={()=>void op.run(JSON.stringify(payload),async key=>{const j=await unwrap(api.post("/revenant/r8/runs/{id}/counterfactual",
            {path:{id:state.runId},body:payload,idempotencyKey:key}));chooseJob(j.jobId);})}>Run sandbox replay</button></div>}
      <ErrorText error={op.error||jobError}/>{op.note&&<p role="status">{op.note}</p>}
      {job&&<p role="status" data-testid="r8-job-status">Replay {job.status} {job.failure&&`· ${job.failure}`}</p>}
      {pending&&job&&<button disabled={op.busy} onClick={()=>void op.run("cancel:"+job.jobId,async key=>{
        await unwrap(api.post("/revenant/r8/runs/{id}/counterfactual/{job}/cancel",{path:{id:state.runId,job:job.jobId},idempotencyKey:key}));await reloadJob();})}>Cancel replay</button>}
    </Panel>
    {job?.status==="Verified"&&job.verified&&<Comparison value={job.verified} opening={state.opening}/>}
  </>;
}

function Comparison({value, opening}: {value: Verified; opening: Balance[]}) {
  const [bank,setBank]=useState("BANK-A"),[field,setField]=useState<"availableMinor"|"postedMinor"|"reservedMinor">("availableMinor");
  const b=value.balances.filter(x=>x.bankId===bank);
  const reference = field === "availableMinor" ? bufferReference(opening, bank) : undefined;
  const metric=(x:S["R8Headline"],key:"source"|"alternative"|"delta")=>x.unit==="USD minor units"?money(x[key],key==="delta"):key==="delta"?signedInteger(x[key]):x[key];
  return <Panel name="Verified financial comparison"><p className="r8-muted">{value.sourcePolicy} → {value.alternativePolicy} · {value.mode==="PolicyOnly"?"Policy only":"Policy + sandbox intervention"} · {value.costPolicy}</p>
    <div className="r8-metrics r8-kpis">{value.headlines.map(x=><div key={x.label} data-testid="r8-kpi" data-delta={x.delta}><small>{x.label}</small>
      <strong>{metric(x,"delta")}</strong><span>{metric(x,"source")} → {metric(x,"alternative")}</span></div>)}</div>
    <div className="r8-actions"><BankSelect value={bank} onChange={setBank}/><label>Balance <select value={field} onChange={e=>setField(e.target.value as typeof field)}>
      <option value="availableMinor">Available</option><option value="postedMinor">Posted</option><option value="reservedMinor">Reserved</option></select></label></div>
    <Chart reference={reference} label={bank+" source and completed alternative balances"} series={[
      {name:"Source history",color:"#e5e6d9",values:b.map(x=>({at:x.atMs,minor:x.source[field]}))},
      {name:"Verified sandbox",color:"#86bcf6",dash:true,values:b.map(x=>({at:x.atMs,minor:x.alternative[field]}))},
    ]}/>
    <details><summary>Aligned balance data</summary>{reference && <p>Shared operating buffer reference: {reference.status === "Available" ? money(reference.minor) : "Unavailable"}</p>}<Table headers={["Time","Source","Alternative"]} rows={b.map(x=>[time(x.atMs),money(x.source[field]),money(x.alternative[field])])}/></details>
    <Table headers={["Outcome","Source","Alternative","Delta"]} rows={value.breakdown.map(x=>[x.label,metric(x,"source"),metric(x,"alternative"),metric(x,"delta")])}/>
    <details><summary>Inspect underlying payments and evidence</summary><Table headers={["Intent","Principal","Source","Alternative","Cause","Delay","Evidence"]} rows={value.payments.map(p=>[
      p.intentId,money(p.principalMinor),p.sourceStatus,p.alternativeStatus,p.alternativeCause,p.delayMinutes+" min",
      <details><summary>Screening digests</summary><code>{p.sourceEvidence??"Unavailable"}</code><code>{p.alternativeEvidence??"Unavailable"}</code></details>])}/></details>
    <p className="r8-integrity">Verified branch <code>{value.childTimelineId}</code></p>
  </Panel>;
}
function Audit({api,id,result}:{api:Api;id:string;result:Verified|null}) {
  const audit=useLoad(useMemo(()=>()=>unwrap(api.get("/revenant/r8/runs/{id}/audit",{path:{id}})),[api,id]));
  const value=audit.value;
  return <><Panel name="Source verification"><ErrorText error={audit.error}/>{!value&&!audit.error&&<p role="status">Verifying source history…</p>}
    {value&&<><p className={value.status==="Verified"&&value.ledgerVerification==="Verified"?"r8-integrity":""}>{value.status} Timeline · {value.ledgerVerification} ledger chain</p>
      <Facts rows={[["Timeline",value.timelineId],["State hash",value.stateHash],["Record hash",value.recordHash],
        ["Manifest",value.manifestHash],["Policy",value.policy],["Model",value.model],["Ledger audit head",value.ledgerHeadHash]]}/>
      <details><summary>Recent source events</summary><Table headers={["Sequence","Event","Actor","Subject","Version","Evidence"]} rows={value.recentEvents.map(e=>[
        e.sequence,e.event,e.actor,e.subject,e.subjectVersion??"—",<code>{e.evidenceSha256??"—"}</code>])}/></details></>}</Panel>
    {result&&<Panel name="Sandbox verification">{value&&value.status==="Verified"&&value.recordHash===result.sourceRecordHash&&value.stateHash===result.sourceStateHash&&
      <p className="r8-integrity">Original history unchanged</p>}
      <Facts rows={[["Child Timeline",result.childTimelineId],["Child record",result.childRecordHash],["Child state",result.childStateHash],
        ["Result evidence",result.evidenceDigest],["Policies",result.sourcePolicy+" → "+result.alternativePolicy],
        ["Cost assumptions",result.costPolicy],["Continuation rule",result.continuationPolicy]]}/>
      <Table headers={["Intent","Reviewer","Time","Action","Disposition"]} rows={result.actions.map(a=>[
        a.intentId,a.actor,time(a.atMs),a.action,a.status+" · "+a.code])}/></Panel>}</>;
}
function Facts({rows}:{rows:string[][]}) {return <dl className="r8-facts">{rows.map(([name,value])=><div key={name}><dt>{name}</dt><dd><code>{value}</code></dd></div>)}</dl>;}
