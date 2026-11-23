import { useState, type CSSProperties } from "react";
import { TimeChart, type LaneMarker, type Series } from "./charts.tsx";
import { clock, demoMoney, hourMinute, time } from "./format.ts";
import { branchAlternate } from "./demoBranch.ts";
import { BankSelect } from "./Liquidity.tsx";
import { preparedRewind } from "./demo/fixtures.ts";
import { RewindParticles } from "./RewindParticles.tsx";
import type { Stage } from "./RewindDeck.tsx";
import { Drawer, Table, type S, type Verified } from "./shared.tsx";

type Field = "availableMinor" | "postedMinor" | "reservedMinor";
const fieldName: Record<Field, string> = { availableMinor: "Available", postedMinor: "Posted", reservedMinor: "Reserved" };
const ORDER: Stage[] = ["original", "analysing", "hold", "dissolve", "void", "rescale", "emerge", "settle", "done"];
const at = (stage: Stage) => ORDER.indexOf(stage);

/**
 * Demo-only Rewind chart. The same TimeChart draws the original history and the two-future comparison with the prepared
 * numbers. A canvas particle layer (children of the chart) carries the transformation; the real SVG curves, areas and result
 * labels are revealed underneath once the swarm has settled. Layout, axes, data and cards are the existing ones.
 */
export function RewindStage({ value, stage, alternate, forkAt, confirmedAt, previewAt, pulse, reveal, markers, advance }: {
  value: Verified; stage: Stage; alternate: boolean; /** fork the displayed run was started from */ forkAt: number; /** fork confirmed on the Timeline */ confirmedAt: number;
  /** live scrub position */ previewAt: number; pulse: number; /** graph-only playback progress, null when not replaying */ reveal: number | null;
  markers: LaneMarker[]; advance: (from: Stage) => void;
}) {
  const [bank, setBank] = useState(preparedRewind.bank);
  const [field, setField] = useState<Field>("availableMinor");
  const compare = alternate && at(stage) >= at("rescale");
  const network = bank === preparedRewind.bank;
  const b = value.balances.filter(x => x.bankId === bank);
  const f: Field = compare ? field : "availableMinor";
  const alternative = "Prepared alternate";
  const series: Series[] = [{ name: "Source history", tone: "source", area: true, values: b.map(x => ({ at: x.atMs, minor: x.source[f] })) }];
  // The prepared 09:00 comparison is used exactly as approved. Any other minute adapts the same prepared alternate for presentation only.
  const branch = compare && forkAt !== preparedRewind.fork.atMs
    ? branchAlternate(b.map(x => ({ at: x.atMs, source: BigInt(x.source[f]), alternative: BigInt(x.alternative[f]) })), preparedRewind.fork.atMs, forkAt) : null;
  if (compare) series.push({ name: alternative, tone: "alternative", values: branch ? branch.series : b.filter(x => x.atMs >= forkAt).map(x => ({ at: x.atMs, minor: x.alternative[f] })) });
  // Before a test the line sits on the confirmed fork; once a run starts it stays on that run's fork until a new test begins.
  const shown = stage === "original" ? confirmedAt : forkAt;
  const fork = { at: shown, label: `FORK ${clock(shown)}` };
  const idle = stage === "original" || stage === "done";
  const preview = idle ? { at: previewAt, label: hourMinute(previewAt) } : null;
  const ready = idle && stage !== "original" && confirmedAt !== forkAt ? { at: confirmedAt, label: `READY · ${hourMinute(confirmedAt)}` } : null;
  const callouts = b.length ? [...(compare ? [{ series: alternative, label: `ALT · ${value.alternativePolicy}` }] : []), { series: "Source history", label: `ORIGINAL · ${value.sourcePolicy}` }] : [];
  const layer = stage === "dissolve" || stage === "emerge" || stage === "settle";
  const metric = (x: S["R8Headline"], key: "source" | "alternative" | "delta") => x.unit === "USD minor units" ? demoMoney(x[key], key === "delta" && /net/i.test(x.label)) : x[key];
  const heroIndex = Math.max(0, value.headlines.findIndex(h => /net/i.test(h.label)));
  const lit = alternate && stage === "done";

  return <div className={"r8-compare is-stage" + (lit ? " is-compare" : "")}>
    <section className="r8-card r8-rewind-chart" aria-label="Rewind financial comparison">
      <div className="r8-card-head"><h2 className="r8-eyebrow">{network ? "Network position" : bank + " " + fieldName[f].toLowerCase()} · {compare ? "original vs prepared alternate" : "original history"}</h2>
        <div className="r8-chips"><BankSelect value={bank} onChange={setBank} network />
          {compare && <label className="r8-select r8-fade-in"><span className="r8-sr">Balance</span><select aria-label="Balance" value={field} onChange={e => setField(e.target.value as Field)}>
            <option value="availableMinor">Available</option><option value="postedMinor">Posted</option><option value="reservedMinor">Reserved</option></select></label>}
          <span className="r8-chip">ORIGINAL · {value.sourcePolicy}</span>{compare && <span className="r8-chip is-coral r8-fade-in">ALTERNATE · {value.alternativePolicy}</span>}</div></div>
      <div className="r8-rewind-plot" data-fx={stage}>
        <TimeChart showNote={false} label={bank + " original and alternate balances"} yLabel={network ? "USD · network position" : `USD · committed ${fieldName[f].toLowerCase()} balance`}
          markers={markers} exactMoney={demoMoney} fork={fork} preview={preview} ready={ready} pulse={pulse} reveal={reveal}
          wedge={compare ? { a: alternative, b: "Source history", from: forkAt } : null} callouts={callouts} series={series}>
          {layer && <RewindParticles mode={stage === "dissolve" ? "dissolve" : "emerge"} settle={stage === "settle"} onDone={() => advance(stage)} />}
        </TimeChart>
      </div>
      <div className="r8-chart-foot">
        <p className="r8-muted">06:00–12:00 UTC · lane: ▲ screening · ● other facts · | seals{compare ? " · compared segment from the fork" : ""}</p>
        <div className="r8-drawers">
          <Drawer summary={compare ? "Aligned balance data" : "View as table"} className="is-up">
            <Table caption={`${bank} balances`} headers={compare ? ["Time", "Source", "Alternative"] : ["Seal time", "Available"]}
              rows={b.map((x, i) => compare ? [time(x.atMs), demoMoney(x.source[f]), demoMoney(branch ? branch.aligned[i]!.toString() : x.alternative[f])] : [time(x.atMs), demoMoney(x.source[f])])} />
          </Drawer>
          {compare && <>
            <Drawer summary="Outcome breakdown" className="is-up">
              <Table headers={["Outcome", "Source", "Alternative", "Delta"]} rows={value.breakdown.map(x => [x.label, metric(x, "source"), metric(x, "alternative"), metric(x, "delta")])} />
              <p className="r8-muted">{value.costPolicy} · {value.continuationPolicy}</p>
            </Drawer>
            <Drawer summary="Inspect underlying payments and evidence" className="is-up">
              <Table headers={["Intent", "Principal", "Source", "Alternative", "Cause", "Delay"]} rows={value.payments.map(p => [
                p.intentId, demoMoney(p.principalMinor), p.sourceStatus, p.alternativeStatus, p.alternativeCause, p.delayMinutes + " min"])} />
            </Drawer>
          </>}
        </div>
      </div>
    </section>
    {compare && <section className="r8-kpi-col" aria-label="Prepared economic outcome" aria-hidden={!lit}>
      {value.headlines.map((x, i) => <div key={x.label} data-testid="r8-kpi" data-delta={x.delta} className={"r8-quad" + (i === heroIndex ? " is-hero" : "") + " is-k" + i}
        style={{ order: i === heroIndex ? -1 : i, "--enter": i === heroIndex ? 0 : 1 + i } as CSSProperties}>
        <div className="r8-eyebrow">{x.label}</div>
        <p className="r8-figure">{metric(x, "delta")}{x.unit === "payments" && <small>{x.unit}</small>}</p>
        <span className="r8-quad-delta">{metric(x, "source")} → {metric(x, "alternative")}</span>
        <span className="r8-quad-context">Illustrative alternative-policy estimate</span>
      </div>)}
    </section>}
  </div>;
}
