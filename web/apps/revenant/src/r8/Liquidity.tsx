import { useMemo, useState } from "react";
import { preparedForecast, preparedRewind } from "./demo/fixtures.ts";
import { bufferReference, type BufferReference } from "./bufferReference.ts";
import { BufferNote, placementOf, TimeChart, type Series } from "./charts.tsx";
import { BANKS, bankAlias, clock, compactMoney, demoMillions, demoMoney, hourMinute, money, time } from "./format.ts";
import { Drawer, ErrorText, Icon, Table, unwrap, useFinanceRefresh, useLoad, type Api, type Snapshot } from "./shared.tsx";

const demo = import.meta.env.REVENANT_DEMO === true;
const amount = demo ? demoMoney : money;
const positionMoney = demo ? demoMillions : compactMoney;

export function BankSelect({ value, onChange, network }: { value: string; onChange: (s: string) => void; network?: boolean }) {
  return <label className="r8-select"><span className="r8-sr">Bank</span><select aria-label="Bank" value={value} onChange={e => onChange(e.target.value)}>
    {network && <option value={preparedRewind.bank}>Network</option>}{BANKS.map(b => <option key={b.id} value={b.id}>{b.id}</option>)}</select></label>;
}

export function Liquidity({ id, api, state, live }: { id: string; api: Api; state: Snapshot; live: boolean }) {
  const data = useLoad(useMemo(() => () => unwrap(api.get("/revenant/r8/runs/{id}/liquidity", { path: { id } })), [api, id]));
  useFinanceRefresh(live, data.reload);
  const [bank, setBank] = useState(demo ? preparedForecast.bank : "BANK-A");
  const alias = bankAlias(bank);
  const forecast = demo && bank === preparedForecast.bank ? preparedForecast : null;
  const reference: BufferReference = forecast
    ? { status: "Available", minor: forecast.thresholdMinor, openingMinor: state.opening.find(b => b.bankId === bank)!.availableMinor }
    : bufferReference(state.opening, bank);
  const actual = data.value?.actual.filter(x => x.balance.bankId === bank) ?? [];
  const future = data.value?.projection.filter(x => x.bankId === bank) ?? [];
  const last = actual.at(-1) ?? null;
  const latestByBank = (b: string) => data.value?.actual.filter(x => x.balance.bankId === b).at(-1)?.balance ?? state.balances.find(x => x.bankId === b) ?? null;
  const current = last?.balance ?? state.balances.find(x => x.bankId === bank) ?? null;
  const series: Series[] = [
    { name: "Available", tone: "source", area: true, values: actual.map(x => ({ at: x.atMs, minor: x.balance.availableMinor })) },
    ...(demo ? forecast ? [{ name: "Median forecast", tone: "projection", dash: true, hollow: true, values: forecast.median } as const] : [] : [
    { name: "Posted", tone: "posted", values: actual.map(x => ({ at: x.atMs, minor: x.balance.postedMinor })) },
    { name: "Reserved", tone: "reserved", dash: true, values: actual.map(x => ({ at: x.atMs, minor: x.balance.reservedMinor })) },
    { name: "Persistence baseline", tone: "projection", dash: true, hollow: true,
      values: [...(last ? [{ at: last.atMs, minor: last.balance.availableMinor }] : []), ...future.map(x => ({ at: x.atMs, minor: x.availableMinor }))] },
    ] satisfies Series[]),
  ];

  return <div className="r8-fan">
    <div className="r8-heading">
      <div><div className="r8-eyebrow">{demo ? "LIQUIDITY · PREPARED SCENARIO" : "LIQUIDITY · LEDGER & PERSISTENCE"}</div><h1>Know what is posted, reserved and available.</h1></div>
      <p className="r8-heading-note">Snapshot · {clock(state.boundaryMs)} UTC <span>·</span> {demo ? "prepared observations" : "eight-point persistence reference"}</p>
    </div>
    <div className="r8-fan-grid">
      <section className="r8-card r8-fan-chart" aria-label="Bank liquidity">
        <div className="r8-card-head">
          <h2 className="r8-eyebrow">{alias.name} · available liquidity <small>{bank}</small></h2>
          <div className="r8-chips"><span className="r8-chip">{demo ? "OBSERVED" : "OBSERVED LEDGER"}</span>{(!demo || forecast) && <span className="r8-chip">{forecast ? "50% & 80% BANDS" : "8-POINT PERSISTENCE"}</span>}<BankSelect value={bank} onChange={setBank} /></div>
        </div>
        <ErrorText error={data.error} />
        {!data.value && !data.error && <p className="r8-loading" role="status">Loading completed bank observations…</p>}
        {data.value && <>
          <div className="r8-fan-plot">
            <TimeChart reference={demo && !forecast ? undefined : reference} showNote={false}
              label={forecast ? "Corvane observed liquidity and prepared forecast with 50% and 80% bands" : bank + (demo ? " observed liquidity" : " posted, reserved, available and projected available liquidity")}
              yLabel={demo ? "USD · available liquidity" : "USD · committed balance"}
              bands={forecast ? [{ ...forecast.outer, label: "80% band", opacity: 0.12 }, { ...forecast.inner, label: "50% band", opacity: 0.22 }] : undefined}
              referenceLabel={forecast ? "MINIMUM LIQUIDITY" : undefined}
              axisMoney={demo ? demoMillions : undefined} exactMoney={amount}
              now={last ? { at: last.atMs, label: hourMinute(last.atMs) } : null}
              callouts={last ? [{ series: "Available", label: "ACTUAL AVAILABLE" }] : []}
              series={series} />
          </div>
          <div className="r8-chart-foot">
            <span className="r8-legend"><i className="is-source" />Available {demo ? forecast && <><i className="is-projection" />Median forecast · 50% / 80% bands</> :
              <><i className="is-posted" />Posted <i className="is-reserved" />Reserved <i className="is-projection" />Persistence (+5 … +40 min)</>}</span>
            <p className="r8-muted">{demo ? forecast && `Prepared forecast through ${time(forecast.horizon)}` : `${data.value.method} · statistical baseline, ${data.value.uncertainty} · anomaly: ${data.value.anomalyStatus}`}</p>
            <Drawer summary="Liquidity data table" className="is-up">
              {(!demo || forecast) && <p>{forecast ? "Minimum liquidity" : "Operating buffer reference"} · {reference.status === "Available" ? amount(reference.minor) : "Unavailable"}</p>}
              <Table caption={`${bank} observed balances`} headers={["Time", "Posted", "Reserved", "Available"]} rows={actual.map(x => [
                time(x.atMs), amount(x.balance.postedMinor), amount(x.balance.reservedMinor), amount(x.balance.availableMinor)])} />
              {forecast ? <Table caption="Corvane prepared forecast" headers={["Time", "Median", "50% lower", "50% upper", "80% lower", "80% upper"]} rows={forecast.median.map((p, i) => [
                time(p.at), amount(p.minor), amount(forecast.inner.lower[i]!.minor), amount(forecast.inner.upper[i]!.minor),
                amount(forecast.outer.lower[i]!.minor), amount(forecast.outer.upper[i]!.minor),
              ])} /> : !demo && <Table caption={`${bank} persistence baseline`} headers={["Projection time", "Available baseline"]} rows={future.map(x => [time(x.atMs), money(x.availableMinor)])} />}
            </Drawer>
          </div>
        </>}
      </section>
      <div className="r8-fan-side">
        <section className="r8-card r8-limit" aria-label={demo ? "Prepared liquidity scenario" : "Model limitation"}>
          {demo ? <>
            <div className="r8-eyebrow is-coral">PREPARED FORECAST</div>
            {forecast ? <>
              <h2>Corvane liquidity could reach the floor at {time(forecast.crossing)}.</h2>
              <p>Last observed at {last ? time(last.atMs) : "09:40 UTC"} · {current ? amount(current.availableMinor) : "—"} available.</p>
              <div className="r8-buffer-note"><strong>Minimum liquidity</strong><span>{amount(forecast.thresholdMinor)}</span><small>Illustrative 50% and 80% bands through {time(forecast.horizon)}.</small></div>
            </> : <h2>{alias.name} · observed liquidity</h2>}
          </> : <>
          <div className="r8-eyebrow is-coral">STATISTICAL MODEL · LIMITED</div>
          <h2>A persistence reference, not a crisis forecast.</h2>
          <p>Eight equal-value points extend the last observed available balance at five-minute steps. No confidence band is computed; the method is {data.value?.uncertainty?.toLowerCase() ?? "uncalibrated"}.</p>
          <BufferNote reference={reference} placement={data.value ? placementOf(series, reference) : undefined} />
          </>}
          <a className="r8-link" href="#held">Review related held instructions <Icon name="arrow" /></a>
        </section>
        <section className="r8-card r8-positions-table" aria-label="Bank positions">
          <div className="r8-card-head"><h2 className="r8-eyebrow">BANK POSITIONS</h2><span className="r8-muted">USD · select bank</span></div>
          <div className="r8-scroll" tabIndex={0}><table>
            <thead><tr><th scope="col">Bank</th><th scope="col" aria-label="Posted balance">Posted</th><th scope="col" aria-label="Reserved balance">Reserved</th><th scope="col" aria-label="Available balance">Available</th></tr></thead>
            <tbody>{BANKS.map(b => { const v = latestByBank(b.id); return <tr key={b.id} className={b.id === bank ? "is-selected" : ""}>
              <th scope="row"><label className="r8-check"><input type="radio" name="r8-fan-bank" checked={b.id === bank} onChange={() => setBank(b.id)} aria-label={`Show ${b.id}`} />
                <b>{b.code}</b><small>{b.id}</small></label></th>
              <td title={v ? amount(v.postedMinor) : ""}>{v ? positionMoney(v.postedMinor) : "—"}</td>
              <td className="is-muted" title={v ? amount(v.reservedMinor) : ""}>{v ? compactMoney(v.reservedMinor) : "—"}</td>
              <td title={v ? amount(v.availableMinor) : ""}>{v ? positionMoney(v.availableMinor) : "—"}</td></tr>; })}</tbody></table></div>
        </section>
      </div>
      <div className="r8-fan-tiles">
        {(["Posted", "Reserved", "Available"] as const).map(k => <div key={k} className="r8-card r8-fan-tile">
          <div className="r8-fan-tile-top"><span className="r8-eyebrow">{k} · {alias.code}</span><span className="r8-muted">{last ? `observed ${time(last.atMs)}` : "committed snapshot"}</span></div>
          <p className="r8-figure">{current ? amount(current[(k.toLowerCase() + "Minor") as "postedMinor"]) : "—"}<small> USD</small></p></div>)}
      </div>
    </div>
  </div>;
}
