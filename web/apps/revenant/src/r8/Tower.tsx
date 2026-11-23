import { useMemo, useState } from "react";
import { bufferReference } from "./bufferReference.ts";
import { TimeChart } from "./charts.tsx";
import { preparedForecast, preparedTower } from "./demo/fixtures.ts";
import { demoMillions, demoMoney, BANKS, bankAlias, bankCode, clock, compactMoney, money, sumMinor, time } from "./format.ts";
import { CityMap } from "./CityMap.tsx";
import { Icon, isOpenCase, unwrap, useFinanceRefresh, useLoad, type Api, type Balance, type Payment, type Snapshot } from "./shared.tsx";

const demo = import.meta.env.REVENANT_DEMO === true;
const amount = demo ? demoMoney : money;

// Basin centres from the approved reservoir artwork (viewBox 1010 × 407), keyed by the real bank id.
const basin: Record<string, [number, number]> = {
  "BANK-A": [124, 191], "BANK-B": [331, 86], "BANK-C": [684, 86], "BANK-D": [331, 294], "BANK-E": [905, 191], "BANK-F": [684, 294],
};
const ring = "M124 191Q219 86 331 86H684Q816 86 905 191Q818 294 684 294H331Q207 294 124 191";

/**
 * Water level rule (documented in the figure caption): a basin's water height is that bank's committed available balance
 * divided by the largest committed posted balance among the six banks, clamped to 6–94 % of the basin so empty and full
 * banks remain legible. It is a proportional picture of real balances, not a forecast.
 */
function level(available: bigint, scale: bigint) {
  if (scale <= 0n || available <= 0n) return 0.06;
  return Math.min(0.94, Math.max(0.06, Number((available * 10000n) / scale) / 10000));
}

export function Tower({ api, state, live, openCase }: { api: Api; state: Snapshot; live: boolean; openCase: (intent: string) => void }) {
  const liquidity = useLoad(useMemo(() => () => unwrap(api.get("/revenant/r8/runs/{id}/liquidity", { path: { id: state.runId } })), [api, state.runId]));
  useFinanceRefresh(live, liquidity.reload);
  const openCases = state.cases.filter(isOpenCase);
  const settled = state.payments.filter(p => p.status === "Settled");
  const held = state.payments.filter(p => p.status === "Held");
  const reserved = sumMinor(state.balances.map(b => b.reservedMinor));
  const focusCase = openCases[0] ?? null;
  const focusPayment = focusCase ? state.payments.find(p => p.intentId === focusCase.intentId) ?? null : null;
  const defaultBank = focusPayment?.sourceBank
    ?? state.balances.find(b => { const o = state.opening.find(x => x.bankId === b.bankId); return o && BigInt(b.availableMinor) < BigInt(o.availableMinor); })?.bankId
    ?? "BANK-A";
  const [bank, setBank] = useState(defaultBank);
  const awaiting = openCases.filter(c => c.status === "AwaitingSecondApproval").length;
  const heading = demo ? preparedTower.heading : openCases.length === 0
    ? "Money is moving across six banks; nothing is held for review."
    : `Money is moving across six banks; ${openCases.length === 1 ? "one held payment asks" : `${openCases.length} held payments ask`} for review.`;

  const primaryOpen = demo && openCases.some(c => c.intentId === preparedTower.alerts[2]!.intent);
  const useCompactTrend = demo && bank === "BANK-A";

  const callout = focusPayment && focusCase ? <>
    <div className="r8-callout-title">Held payment awaiting review<span>· {focusPayment.intentId} · {amount(focusPayment.amountMinor)}</span></div>
    {demo ? <div className="r8-callout-path">MRD <b>→</b> OST <b>→</b> CRV <b>→</b> MRD <small>circular pattern</small></div>
      : <div className="r8-callout-path">{bankCode(focusPayment.sourceBank)} <b>→</b> {bankCode(focusPayment.destinationBank)} <small>{focusPayment.sourceBank} → {focusPayment.destinationBank}</small></div>}
    <p>{focusCase.status === "AwaitingSecondApproval" ? "Awaiting second approval" : demo ? "Held" : "Open"} · {focusCase.approvalCount} of {focusCase.requiredApprovals} approvals</p>
    <button className="r8-icon-btn" aria-label={`Open case ${focusCase.intentId}`} onClick={() => openCase(focusCase.intentId)}><Icon name="arrow" /></button>
  </> : <><div className="r8-callout-title">No held payment needs review<span>· {settled.length} settled flows drawn from committed payments</span></div></>;
  return <div className="r8-tower">
    <div className="r8-heading">
      <div>
        {!demo && <div className="r8-eyebrow">FLOW MONITOR / {state.status === "Verified" ? "VERIFIED SOURCE SESSION" : "ACTIVE SESSION"}</div>}
        <h1>{heading}</h1>
      </div>
      <p className="r8-heading-note">As of {clock(state.boundaryMs)} UTC <span>·</span> scenario clock <span>·</span> Synthetic</p>
    </div>
    <section className="r8-kpis-strip" aria-label="Run totals">
      {demo ? <>
        <Kpi label="Volume today" value={amount(preparedTower.volumeMinor)} status="On pace" desc={`· Across ${preparedTower.transferCount} routed transfers`} />
        <Kpi label="Held amount" value={amount(sumMinor(openCases.map(c => c.amountMinor)))} status="Under review" desc={`· ${openCases.length} payments currently held`} />
        <Kpi label="Open alerts" value={String(openCases.length)} status="Needs review" desc={`· ${primaryOpen ? 3 : 2} high priority`} />
        <Kpi label="Release rate" value={preparedTower.releaseRate} status="Healthy" desc={`· Versus ${preparedTower.priorReleaseRate} yesterday`} />
      </> : <>
      <Kpi label="Settled principal" value={money(sumMinor(settled.map(p => p.amountMinor)))} status={`${settled.length} settled`} desc={`of ${state.payments.length} accepted payments in this run`} />
      <Kpi label="Reserved funds" value={money(reserved)} status={`● ${held.length} held`} desc="held instructions · reserve ledger" />
      <Kpi label="Open review cases" value={String(openCases.length)} status={openCases.length ? "▼ Needs review" : "Clear"} desc={`${awaiting} awaiting second approval · ${state.cases.length} cases total`} />
      <Kpi label="Accepted payments" value={String(state.payments.length)} status={`${state.payments.filter(p => p.status === "Returned").length} returned`}
        desc={`${state.payments.filter(p => !["Settled", "Held", "Returned"].includes(p.status)).length} in other states`} />
      </>}
    </section>
    <div className={"r8-tower-middle" + (demo ? " is-city" : "")}>
      <section className={"r8-hero-map" + (demo ? " is-city" : "")} aria-label={demo ? "Six-bank city map" : "Six-bank reservoir map"}>
        {demo ? <CityMap state={state} focus={focusPayment}>{({ w, h, mid }) => {
          const cw = 250, left = Math.max(12, Math.min(mid[0] + 62, w - cw - 150)), top = Math.max(12, Math.min(h * 0.56, h - 150));
          return <>
            {focusPayment && focusCase && <svg className="r8-city-lead" width={w} height={h} aria-hidden="true"><path d={`M${mid[0]} ${mid[1]}L${left} ${top + 30}`} /></svg>}
            <div className={"r8-map-callout" + (focusPayment ? "" : " is-calm")} style={{ left, top, width: cw }}>{callout}</div>
          </>;
        }}</CityMap> : <div className="r8-map-stage"><div className="r8-map-frame">
        <Reservoirs state={state} focus={focusPayment} />
        <div className={"r8-map-callout" + (focusPayment ? "" : " is-calm")}>{callout}</div></div></div>}
        <div className="r8-map-pill">Scenario boundary <strong>{clock(state.boundaryMs)} UTC</strong></div>
        <a className="r8-link r8-map-link" href="#audit">Review earlier decisions <Icon name="arrow" /></a>
      </section>
      <Alerts state={state} openCase={openCase} />
    </div>
    <div className="r8-rail">
      <section className="r8-rail-cell" aria-label="Current balances">
        <div className="r8-rail-title"><h2>{demo ? "Net Bank Positions" : "Current balances · posted"}</h2><span>{demo ? "USD · PREPARED" : "USD · COMMITTED"}</span></div>
        <div className="r8-positions">{BANKS.map(b => {
          const bal = state.balances.find(x => x.bankId === b.id), open = state.opening.find(x => x.bankId === b.id);
          const below = bal && open && BigInt(bal.availableMinor) < BigInt(open.availableMinor);
          return <button key={b.id} className="r8-position" aria-pressed={bank === b.id} onClick={() => setBank(b.id)} title={`${b.id} · posted ${bal ? money(bal.postedMinor) : "—"} · reserved ${bal ? money(bal.reservedMinor) : "—"} · available ${bal ? money(bal.availableMinor) : "—"}`}>
            <span><b>{b.code}</b><i>·</i>{b.name}</span>{(demo ? b.id === preparedForecast.bank : below) && <em>{demo ? "▼ Declining liquidity" : "▼ Below opening"}</em>}<strong>{bal ? (demo ? demoMillions(bal.availableMinor) : money(bal.postedMinor)) : "—"}</strong></button>;
        })}</div>
      </section>
      <section className="r8-rail-cell r8-rail-chart" aria-label="Selected bank trend">
        <BankTrend bank={bank} opening={useCompactTrend ? preparedTower.trendOpening : state.opening} points={useCompactTrend ? preparedTower.trends : liquidity.value?.actual ?? []} error={liquidity.error} />
      </section>
      <section className="r8-rail-cell" aria-label="Recent activity">
        <div className="r8-rail-title"><h2>Recent activity</h2><span>LATEST FIRST · {live ? "POLLED" : "STATIC"}</span></div>
        <Activity payments={demo ? preparedTower.activity : state.payments} cases={state.cases.map(c => c.intentId)} openCase={openCase} />
      </section>
    </div>
  </div>;
}

function Kpi({ label, value, status, desc }: { label: string; value: string; status: string; desc: string }) {
  return <div className="r8-kpi-tile"><div className="r8-kpi-label">{label}</div><div className="r8-kpi-value">{value}</div>
    <div className="r8-kpi-meta"><b>{status}</b><span title={demo ? desc : undefined}>{desc}</span></div></div>;
}

function Reservoirs({ state, focus }: { state: Snapshot; focus: Payment | null }) {
  const scale = state.balances.reduce((m, b) => (BigInt(b.postedMinor) > m ? BigInt(b.postedMinor) : m), 0n);
  const flows = state.payments.filter(p => basin[p.sourceBank] && basin[p.destinationBank] && p.sourceBank !== p.destinationBank);
  return <svg className="r8-network" viewBox="0 0 1010 407" role="img" aria-label="Committed payment flows among the six banks">
    <title>Water level: committed available balance ÷ largest committed posted balance ({scale > 0n ? money(scale.toString()) : "none"}), clamped to 6–94 %. Routes are drawn only for committed payments.</title>
    <defs>
      <clipPath id="r8-basin-clip"><circle r="46" /></clipPath>
      <linearGradient id="r8-water" x1="0" x2="0" y1="1" y2="0"><stop stopColor="#0F4C5C" /><stop offset="1" stopColor="#3F8F9A" /></linearGradient>
      <marker id="r8-arrow" markerHeight="8" markerWidth="8" orient="auto" refX="6" refY="4"><path d="m1 1 6 3-6 3" fill="none" stroke="context-stroke" strokeWidth="1.7" /></marker>
    </defs>
    <g className="r8-net-grid"><path d="M41 42H978M41 374H978" /><text x="41" y="33">NETWORK RESERVOIRS / COMMITTED BALANCES</text><text x="978" y="33" textAnchor="end">06 CONNECTED BASINS</text></g>
    <path className="r8-channel-shell" d={ring} /><path className="r8-channel-core" d={ring} />
    {flows.map(p => {
      const [ax, ay] = basin[p.sourceBank]!, [bx, by] = basin[p.destinationBank]!;
      const mx = (ax + bx) / 2, my = (ay + by) / 2, cx = mx + (505 - mx) * 0.45, cy = my + (190 - my) * 0.45;
      const dx = bx - cx, dy = by - cy, len = Math.hypot(dx, dy) || 1, ex = bx - (dx / len) * 54, ey = by - (dy / len) * 54;
      const sx = ax + ((cx - ax) / (Math.hypot(cx - ax, cy - ay) || 1)) * 52, sy = ay + ((cy - ay) / (Math.hypot(cx - ax, cy - ay) || 1)) * 52;
      const d = `M${sx.toFixed(1)} ${sy.toFixed(1)}Q${cx.toFixed(1)} ${cy.toFixed(1)} ${ex.toFixed(1)} ${ey.toFixed(1)}`;
      const kind = p.status === "Held" ? "held" : (p.status === "Settled" || (demo && p.status === "Released")) ? "settled" : "other";
      return <g key={p.intentId} className={`r8-flow is-${kind}${focus?.intentId === p.intentId ? " is-focus" : ""}`}>
        {kind === "held" && <path className="r8-flow-halo" d={d} />}
        <path className="r8-flow-line" d={d} markerEnd="url(#r8-arrow)"><title>{p.intentId} · {p.sourceBank} → {p.destinationBank} · {p.status} · {amount(p.amountMinor)}</title></path>
      </g>;
    })}
    {BANKS.map(b => {
      const bal = state.balances.find(x => x.bankId === b.id);
      const [x, y] = basin[b.id]!;
      const surface = 47 - level(bal ? BigInt(bal.availableMinor) : 0n, scale) * 94;
      return <g key={b.id}>
        <g className="r8-basin" transform={`translate(${x} ${y})`}>
          <title>{b.name} ({b.id}) · available {bal ? money(bal.availableMinor) : "—"} · posted {bal ? money(bal.postedMinor) : "—"} · reserved {bal ? money(bal.reservedMinor) : "—"}</title>
          <circle className="r8-basin-shadow" cx="3" cy="5" r="50" /><circle className="r8-basin-ground" r="47" />
          <g clipPath="url(#r8-basin-clip)"><path className="r8-water" d={`M-50 ${surface.toFixed(1)} Q-25 ${(surface - 4).toFixed(1)} 0 ${surface.toFixed(1)} T50 ${surface.toFixed(1)} L50 50 H-50Z`} fill="url(#r8-water)" />
            <path className="r8-water-highlight" d={`M-48 ${(surface + 7).toFixed(1)} Q-21 ${(surface + 3).toFixed(1)} 0 ${(surface + 7).toFixed(1)} T49 ${(surface + 7).toFixed(1)}`} /></g>
          <circle className="r8-basin-rim" r="47" /><circle className="r8-basin-inner" r="41" />
          <path className={"r8-basin-notch" + (focus && (focus.sourceBank === b.id || focus.destinationBank === b.id) ? " is-focus" : "")} d="M-52 20H-40" />
          <text className="r8-basin-code" textAnchor="middle" y="7">{b.code}</text>
          <text className="r8-basin-small" textAnchor="middle" y="23">{bal ? (demo ? demoMillions : compactMoney)(bal.availableMinor) : "—"}</text>
        </g>
        <text className="r8-basin-caption" textAnchor="middle" x={x} y={y + (y > 200 ? 64 : y < 100 ? 70 : 72)}>{b.name}</text>
      </g>;
    })}
    <text className="r8-net-flow-label" textAnchor="middle" x="508" y="398">SOLID · SETTLED   DASHED CORAL · HELD   GREY · RETURNED/OTHER</text>
  </svg>;
}

function Alerts({ state, openCase }: { state: Snapshot; openCase: (intent: string) => void }) {
  const open = state.cases.filter(isOpenCase);
  const rows = demo ? preparedTower.alerts.flatMap<{ key: string; tone: string; title: string; sub: string; at: number; action: (() => void) | null }>(({ intent, ...alert }) => {
    if (!intent) return [{ ...alert, action: null }];
    const review = open.find(c => c.intentId === intent);
    return review ? [{ ...alert, title: review.status === "AwaitingSecondApproval" ? "Held payment awaits second approval" : alert.title,
      action: () => openCase(intent) }] : [];
  }) : [
    ...open.map(c => { const p = state.payments.find(x => x.intentId === c.intentId);
      return { key: c.intentId, tone: "high", title: c.status === "AwaitingSecondApproval" ? "Held payment awaits second approval" : "Held payment requires approval",
        sub: `${c.intentId} · ${p ? `${bankCode(p.sourceBank)} → ${bankCode(p.destinationBank)} · ` : ""}${money(c.amountMinor)}`, at: p?.acceptedAtMs ?? null, action: () => openCase(c.intentId) }; }),
    ...state.anomalies.map(a => ({ key: a.evidenceSha256, tone: "warn", title: "Advisory velocity anomaly",
      sub: `${a.subject} · observed ${a.observed} vs expected ${a.expected.toFixed(2)} · score ${a.score.toFixed(3)}`, at: a.atMs, action: null })),
  ];
  return <section className="r8-alerts" aria-label="Priority alerts">
    <div className="r8-alerts-top"><h2>Priority Alerts</h2><a className="r8-link" href="#held">View queue <Icon name="arrow" /></a></div>
    <div className="r8-alert-list">
      {rows.map(r => {
        const body = <><i className="r8-alert-marker" /><span><strong>{r.title}</strong><small>{r.sub}</small></span><time>{r.at === null ? "—" : time(r.at)}</time></>;
        return r.action ? <button key={r.key} className={"r8-alert is-" + r.tone} onClick={r.action}>{body}</button> : <div key={r.key} className={"r8-alert is-" + r.tone}>{body}</div>;
      })}
      {!demo && state.anomalies.length === 0 && <div className="r8-alert is-muted"><i className="r8-alert-marker" /><span><strong>Velocity anomalies · advisory</strong>
        <small>{state.anomalyStatus === "InsufficientHistory" ? "Insufficient history: the advisory detector needs more completed five-minute buckets." : `Detector status: ${state.anomalyStatus}`}</small></span><time>—</time></div>}
      {rows.length === 0 && <p className="r8-muted r8-alert-empty">No held payment or advisory anomaly is open in this run.</p>}
    </div>
    <div className="r8-alert-foot"><span><b /> {open.length === 0 ? "NONE REQUIRE" : open.length === 1 ? "ONE REQUIRES" : `${open.length} REQUIRE`} HUMAN REVIEW</span>
      <span>{String(open.length).padStart(2, "0")} / {String(state.cases.length).padStart(2, "0")} OPEN</span></div>
  </section>;
}

function BankTrend({ bank, opening, points, error }: { bank: string; opening: Balance[]; points: { atMs: number; balance: Balance }[]; error: string }) {
  const alias = bankAlias(bank);
  const series = points.filter(x => x.balance.bankId === bank).map(x => ({ at: x.atMs, minor: x.balance.availableMinor }));
  const reference = bufferReference(opening, bank);
  const latest = series.at(-1)?.minor ?? null;
  const open = opening.find(o => o.bankId === bank)?.availableMinor ?? null;
  const scale = [latest, open, ...series.map(s => s.minor)].reduce((m, v) => (v !== null && BigInt(v) > m ? BigInt(v) : m), 0n);
  const pct = (v: string | null) => (v === null || scale <= 0n ? 0 : Math.max(0, Math.min(100, Number((BigInt(v) * 1000n) / scale) / 10)));
  return <>
    <div className="r8-rail-title"><h2>{alias.name} · available <small>{bank}</small></h2>
      <span className="r8-legend"><i className="is-source" />Available <i className="is-buffer" />Buffer ref.</span></div>
    <div className="r8-trend">
      {error ? <p className="r8-error" role="alert">{error}</p> :
        <TimeChart compact label={`${bank} available liquidity trend`} series={[{ name: "Available", tone: "source", values: series, area: true }]} reference={reference} showNote={false} />}
      <div className="r8-gauge" title={`Latest available ${latest ? money(latest) : "—"} on a 0–${money(scale.toString())} scale (largest of opening and observed available)`}>
        <span>{compactMoney(scale)}</span>
        <div className="r8-gauge-track"><div className="r8-gauge-water" style={{ height: pct(latest) + "%" }} />
          {reference.status === "Available" && <i style={{ bottom: pct(reference.minor) + "%" }} />}</div>
        <span>$0</span>
      </div>
    </div>
  </>;
}

function Activity({ payments, cases, openCase }: { payments: Payment[]; cases: string[]; openCase: (intent: string) => void }) {
  const rows = [...payments].sort((a, b) => b.acceptedAtMs - a.acceptedAtMs || b.intentId.localeCompare(a.intentId));
  if (rows.length === 0) return <p className="r8-muted r8-rail-empty">No payments accepted yet in this run.</p>;
  return <div className="r8-scroll r8-activity" tabIndex={0}><table>
    <thead className="r8-sr"><tr><th scope="col">Time</th><th scope="col">Intent</th><th scope="col">Route</th><th scope="col">Principal</th><th scope="col">State</th></tr></thead>
    <tbody>{rows.map(p => <tr key={p.intentId}>
      <td className="r8-mono">{time(p.acceptedAtMs).replace(" UTC", "")}</td>
      <td>{cases.includes(p.intentId) ? <button className="r8-text" onClick={() => openCase(p.intentId)}>{p.intentId}</button> : p.intentId}</td>
      <td title={`${p.sourceBank} → ${p.destinationBank}`}>{bankCode(p.sourceBank)} → {bankCode(p.destinationBank)}</td>
      <td className="r8-num">{amount(p.amountMinor)}</td>
      <td className={"r8-state is-" + p.status.toLowerCase()}>{p.status}</td>
    </tr>)}</tbody></table></div>;
}
