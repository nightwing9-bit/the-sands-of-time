import { useMemo, useState } from "react";
import { bufferReference } from "./bufferReference.ts";
import { BufferNote, placementOf, TimeChart, type Series } from "./charts.tsx";
import { BANKS, bankAlias, clock, compactMoney, hourMinute, money, time } from "./format.ts";
import { Drawer, ErrorText, Icon, Table, unwrap, useFinanceRefresh, useLoad, type Api, type Snapshot } from "./shared.tsx";

export function BankSelect({ value, onChange }: { value: string; onChange: (s: string) => void }) {
  return <label className="r8-select"><span className="r8-sr">Bank</span><select aria-label="Bank" value={value} onChange={e => onChange(e.target.value)}>
    {BANKS.map(b => <option key={b.id} value={b.id}>{b.id}</option>)}</select></label>;
}

export function Liquidity({ id, api, state, live }: { id: string; api: Api; state: Snapshot; live: boolean }) {
  const data = useLoad(useMemo(() => () => unwrap(api.get("/revenant/r8/runs/{id}/liquidity", { path: { id } })), [api, id]));
  useFinanceRefresh(live, data.reload);
  const [bank, setBank] = useState("BANK-A");
  const alias = bankAlias(bank);
  const reference = bufferReference(state.opening, bank);
  const actual = data.value?.actual.filter(x => x.balance.bankId === bank) ?? [];
  const future = data.value?.projection.filter(x => x.bankId === bank) ?? [];
  const last = actual.at(-1) ?? null;
  const latestByBank = (b: string) => data.value?.actual.filter(x => x.balance.bankId === b).at(-1)?.balance ?? state.balances.find(x => x.bankId === b) ?? null;
  const current = last?.balance ?? state.balances.find(x => x.bankId === bank) ?? null;
  const series: Series[] = [
    { name: "Available", tone: "source", area: true, values: actual.map(x => ({ at: x.atMs, minor: x.balance.availableMinor })) },
    { name: "Posted", tone: "posted", values: actual.map(x => ({ at: x.atMs, minor: x.balance.postedMinor })) },
    { name: "Reserved", tone: "reserved", dash: true, values: actual.map(x => ({ at: x.atMs, minor: x.balance.reservedMinor })) },
    { name: "Persistence baseline", tone: "projection", dash: true, hollow: true,
      values: [...(last ? [{ at: last.atMs, minor: last.balance.availableMinor }] : []), ...future.map(x => ({ at: x.atMs, minor: x.availableMinor }))] },
  ];

  return <div className="r8-fan">
    <div className="r8-heading">
      <div><div className="r8-eyebrow">LIQUIDITY · LEDGER &amp; PERSISTENCE</div><h1>Know what is posted, reserved and available.</h1></div>
      <p className="r8-heading-note">Snapshot · {clock(state.boundaryMs)} UTC <span>·</span> eight-point persistence reference</p>
    </div>
    <div className="r8-fan-grid">
      <section className="r8-card r8-fan-chart" aria-label="Bank liquidity">
        <div className="r8-card-head">
          <h2 className="r8-eyebrow">{alias.name} · available liquidity <small>{bank}</small></h2>
          <div className="r8-chips"><span className="r8-chip">OBSERVED LEDGER</span><span className="r8-chip">8-POINT PERSISTENCE</span><BankSelect value={bank} onChange={setBank} /></div>
        </div>
        <ErrorText error={data.error} />
        {!data.value && !data.error && <p className="r8-loading" role="status">Loading completed bank observations…</p>}
        {data.value && <>
          <div className="r8-fan-plot">
            <TimeChart reference={reference} showNote={false} label={bank + " posted, reserved, available and projected available liquidity"} yLabel="USD · committed balance"
              now={last ? { at: last.atMs, label: hourMinute(last.atMs) } : null}
              callouts={last ? [{ series: "Available", label: "ACTUAL AVAILABLE" }] : []}
              series={series} />
          </div>
          <div className="r8-chart-foot">
            <span className="r8-legend"><i className="is-source" />Available <i className="is-posted" />Posted <i className="is-reserved" />Reserved <i className="is-projection" />Persistence (+5 … +40 min)</span>
            <p className="r8-muted">{data.value.method} · statistical baseline, {data.value.uncertainty} · anomaly: {data.value.anomalyStatus}</p>
            <Drawer summary="Liquidity data table" className="is-up">
              <p>Operating buffer reference: {reference.status === "Available" ? money(reference.minor) : "Unavailable"}</p>
              <Table caption={`${bank} observed balances`} headers={["Time", "Posted", "Reserved", "Available"]} rows={actual.map(x => [
                time(x.atMs), money(x.balance.postedMinor), money(x.balance.reservedMinor), money(x.balance.availableMinor)])} />
              <Table caption={`${bank} persistence baseline`} headers={["Projection time", "Available baseline"]} rows={future.map(x => [time(x.atMs), money(x.availableMinor)])} />
            </Drawer>
          </div>
        </>}
      </section>
      <div className="r8-fan-side">
        <section className="r8-card r8-limit" aria-label="Model limitation">
          <div className="r8-eyebrow is-coral">STATISTICAL MODEL · LIMITED</div>
          <h2>A persistence reference, not a crisis forecast.</h2>
          <p>Eight equal-value points extend the last observed available balance at five-minute steps. No confidence band is computed; the method is {data.value?.uncertainty?.toLowerCase() ?? "uncalibrated"}.</p>
          <BufferNote reference={reference} placement={data.value ? placementOf(series, reference) : undefined} />
          <a className="r8-link" href="#held">Review related held instructions <Icon name="arrow" /></a>
        </section>
        <section className="r8-card r8-positions-table" aria-label="Bank positions">
          <div className="r8-card-head"><h2 className="r8-eyebrow">BANK POSITIONS</h2><span className="r8-muted">USD · select bank</span></div>
          <div className="r8-scroll" tabIndex={0}><table>
            <thead><tr><th scope="col">Bank</th><th scope="col" aria-label="Posted balance">Posted</th><th scope="col" aria-label="Reserved balance">Reserved</th><th scope="col" aria-label="Available balance">Available</th></tr></thead>
            <tbody>{BANKS.map(b => { const v = latestByBank(b.id); return <tr key={b.id} className={b.id === bank ? "is-selected" : ""}>
              <th scope="row"><label className="r8-check"><input type="radio" name="r8-fan-bank" checked={b.id === bank} onChange={() => setBank(b.id)} aria-label={`Show ${b.id}`} />
                <b>{b.code}</b><small>{b.id}</small></label></th>
              <td title={v ? money(v.postedMinor) : ""}>{v ? compactMoney(v.postedMinor) : "—"}</td>
              <td className="is-muted" title={v ? money(v.reservedMinor) : ""}>{v ? compactMoney(v.reservedMinor) : "—"}</td>
              <td title={v ? money(v.availableMinor) : ""}>{v ? compactMoney(v.availableMinor) : "—"}</td></tr>; })}</tbody></table></div>
        </section>
      </div>
      <div className="r8-fan-tiles">
        {(["Posted", "Reserved", "Available"] as const).map(k => <div key={k} className="r8-card r8-fan-tile">
          <div className="r8-fan-tile-top"><span className="r8-eyebrow">{k} · {alias.code}</span><span className="r8-muted">{last ? `observed ${time(last.atMs)}` : "committed snapshot"}</span></div>
          <p className="r8-figure">{current ? money(current[(k.toLowerCase() + "Minor") as "postedMinor"]) : "—"}<small> USD</small></p></div>)}
      </div>
    </div>
  </div>;
}
