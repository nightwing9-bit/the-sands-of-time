import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { BANKS, bankAlias, bankCode, clock, money, shortHash } from "./format.ts";
import { explainDecisionFailure } from "./decisionErrors.ts";
import { Icon, isOpenCase, unwrap, useOperation, type Api, type Case, type Payment, type Snapshot } from "./shared.tsx";

const PPM_SCALE = 1_000_000;
type Action = "Approve" | "Reject";
type Sort = "score" | "amount" | "newest";

export function Held({ state, api, reload, focus, role }: { state: Snapshot; api: Api; reload: () => Promise<void>; focus: string | null; role: string | null }) {
  const op = useOperation();
  const [selected, setSelected] = useState(focus ?? state.cases.find(isOpenCase)?.intentId ?? state.cases[0]?.intentId ?? "");
  const [query, setQuery] = useState(""), [status, setStatus] = useState("all"), [bank, setBank] = useState("all"), [sort, setSort] = useState<Sort>("score");
  const [decision, setDecision] = useState<Action | null>(null);
  const trigger = useRef<HTMLButtonElement | null>(null);
  const payments = useMemo(() => new Map(state.payments.map(p => [p.intentId, p])), [state.payments]);
  const visible = useMemo(() => {
    const q = query.trim().toLowerCase();
    return state.cases.filter(c => {
      const p = payments.get(c.intentId);
      if (status !== "all" && c.status !== status) return false;
      if (bank !== "all" && p?.sourceBank !== bank && p?.destinationBank !== bank) return false;
      if (!q) return true;
      return [c.intentId, c.caseId, c.status, money(c.amountMinor), p?.sourceBank, p?.destinationBank, p && bankAlias(p.sourceBank).name, p && bankAlias(p.destinationBank).name,
        p && bankCode(p.sourceBank), p && bankCode(p.destinationBank)].some(v => v?.toLowerCase().includes(q));
    }).sort((a, b) => sort === "amount" ? Number(BigInt(b.amountMinor) - BigInt(a.amountMinor) > 0n) - Number(BigInt(b.amountMinor) - BigInt(a.amountMinor) < 0n)
      : sort === "newest" ? (payments.get(b.intentId)?.acceptedAtMs ?? 0) - (payments.get(a.intentId)?.acceptedAtMs ?? 0)
      : (b.risk?.scorePpm ?? -1) - (a.risk?.scorePpm ?? -1));
  }, [state.cases, payments, query, status, bank, sort]);
  const c = state.cases.find(x => x.intentId === selected);
  const p = c ? payments.get(c.intentId) ?? null : null;
  const open = Boolean(c && isOpenCase(c));

  async function commit(action: Action) {
    if (!c) return;
    const target = c;
    const ok = await op.run(action + target.intentId + target.version, async key => {
      try {
        const receipt = await unwrap(api.post("/revenant/r8/runs/{id}/cases/{intent}", { path: { id: state.runId, intent: target.intentId },
          body: { action, expectedVersion: target.version }, idempotencyKey: key }));
        return `Committed ${action.toLowerCase()} for ${target.intentId}: case is now ${receipt.status} (version ${receipt.version}).`;
      } finally { await reload(); }
    });
    if (ok) close();
  }
  function close() { setDecision(null); requestAnimationFrame(() => trigger.current?.focus()); }

  return <div className={"r8-held" + (decision ? " is-dimmed" : "")}>
    <section className="r8-queue" aria-label="Held payment queue" inert={decision ? true : undefined}>
      <div className="r8-queue-head"><h2 className="r8-eyebrow">HELD PAYMENT QUEUE</h2><span className="r8-chip">{state.cases.filter(isOpenCase).length} OPEN · {state.cases.length} CASES</span></div>
      <label className="r8-search"><Icon name="search" /><span className="r8-sr">Search cases</span>
        <input type="search" placeholder="Search payment, case, bank…" value={query} onChange={e => setQuery(e.target.value)} /></label>
      <div className="r8-filters">
        <label><span className="r8-sr">Status filter</span><select aria-label="Status filter" value={status} onChange={e => setStatus(e.target.value)}>
          <option value="all">Status · all</option><option value="Open">Open</option><option value="AwaitingSecondApproval">Awaiting second approval</option>
          <option value="Released">Released</option><option value="Rejected">Rejected</option></select></label>
        <label><span className="r8-sr">Bank filter</span><select aria-label="Bank filter" value={bank} onChange={e => setBank(e.target.value)}>
          <option value="all">Bank · all</option>{BANKS.map(b => <option key={b.id} value={b.id}>{b.code} · {b.id}</option>)}</select></label>
        <label><span className="r8-sr">Sort cases</span><select aria-label="Sort cases" value={sort} onChange={e => setSort(e.target.value as Sort)}>
          <option value="score">Sort · model score</option><option value="amount">Sort · amount</option><option value="newest">Sort · newest</option></select></label>
      </div>
      <div className="r8-queue-sort"><span>Showing {visible.length} of {state.cases.length} synthetic review cases</span><span>{sort === "score" ? "SCORE ↓" : sort === "amount" ? "AMOUNT ↓" : "NEWEST ↓"}</span></div>
      {state.cases.length === 0 ? <p className="r8-queue-empty">No cases have opened. Screening holds appear here as soon as the scenario raises them.</p> :
        <div className="r8-scroll r8-queue-list" tabIndex={0}><table>
          <thead className="r8-sr"><tr><th scope="col">Case</th><th scope="col">Recorded model score</th><th scope="col">Status</th></tr></thead>
          <tbody>{visible.map(x => { const px = payments.get(x.intentId); return <tr key={x.intentId} className={x.intentId === selected ? "is-selected" : ""} aria-selected={x.intentId === selected}>
            <td><button className="r8-case-pick" onClick={() => setSelected(x.intentId)} aria-label={`Select case ${x.intentId}, ${money(x.amountMinor)}`}>
              <strong>{money(x.amountMinor)} <small>USD</small></strong>
              <span>{px ? `${bankCode(px.sourceBank)} → ${bankCode(px.destinationBank)}` : "Route unavailable"} · {x.intentId} · {x.approvalCount}/{x.requiredApprovals}</span></button></td>
            <td><Score risk={x.risk} /></td>
            <td className={"r8-state is-" + x.status.toLowerCase()} title={x.status}>{x.status === "AwaitingSecondApproval" ? "Awaiting 2nd approval" : x.status}</td>
          </tr>; })}</tbody></table>
          {visible.length === 0 && <p className="r8-queue-empty">No loaded case matches these filters.</p>}</div>}
      <div className="r8-queue-foot"><Icon name="shield" />Decisions append to the synthetic audit chain</div>
    </section>

    <div className="r8-case" inert={decision ? true : undefined}>
      {!c ? <div className="r8-case-empty"><p>Select a case to inspect its recorded evidence.</p></div> : <>
        <section className="r8-case-hero" aria-label="Selected case">
          <div className="r8-case-hero-top"><span className="r8-mono">CASE {c.caseId.slice(0, 8).toUpperCase()} · {c.intentId}{p ? ` · ACCEPTED ${clock(p.acceptedAtMs)} UTC` : ""}</span>
            <span className="r8-chip is-glass">{c.status.toUpperCase()}{c.risk ? ` · ${c.risk.policy.toUpperCase()}` : ""}</span></div>
          <p className="r8-case-amount">{money(c.amountMinor)} <small>USD</small></p>
          <p className="r8-case-route">{p ? <><b>{bankCode(p.sourceBank)}</b> / {bankAlias(p.sourceBank).name} <span>→</span> <b>{bankCode(p.destinationBank)}</b> / {bankAlias(p.destinationBank).name}</> : "Route unavailable"}
            <span className="r8-case-sub">· {statusLine(c)}</span></p>
        </section>
        <div className="r8-case-grid">
          <div className="r8-case-col">
            <section className="r8-card is-tonal" aria-label="Payment path">
              <div className="r8-card-head"><h3 className="r8-eyebrow">PAYMENT PATH</h3><span className="r8-chip">BANK ROUTE · 2 NODES</span></div>
              {p ? <div className="r8-path">
                <PathNode letter={bankCode(p.sourceBank)[0]!} name={bankCode(p.sourceBank)} role={`Origin · ${p.sourceBank}`} />
                <span className="r8-path-arrow">→</span>
                <PathNode letter={bankCode(p.destinationBank)[0]!} name={bankCode(p.destinationBank)} role={`Destination · ${p.destinationBank}`} />
                <p className="r8-path-note">Sender and beneficiary accounts are not exposed by the R8 API; only the bank route is shown.</p>
              </div> : <p className="r8-muted">No payment record is loaded for this case.</p>}
            </section>
            <section className="r8-card" aria-label="Payment history">
              <div className="r8-card-head"><h3 className="r8-eyebrow">PAYMENT &amp; CASE FACTS</h3><span className="r8-muted">Only recorded times shown</span></div>
              <div className="r8-history">
                {p && <Fact at={clock(p.acceptedAtMs)} text="Instruction accepted into the scenario" chip={p.status.toUpperCase()} />}
                {c.risk && <Fact at="—" text={`Screened by ${c.risk.policy} · ${c.risk.reason}`} chip={c.risk.scorePpm === null ? "WARMUP" : `${c.risk.scorePpm} PPM`} />}
                {p && !c.risk && <Fact at="—" text={`Screening reason · ${p.reason}`} chip="RULE" />}
                <Fact at="—" text={`Review case opened · version ${c.version}`} chip={c.status.toUpperCase()} />
              </div>
            </section>
            <section className="r8-card" aria-label="Decision history">
              <div className="r8-card-head"><h3 className="r8-eyebrow">DECISION HISTORY</h3><span className="r8-muted">Event sourced</span></div>
              <div className="r8-decision"><i /><div><strong>{c.approvalCount} of {c.requiredApprovals} approvals recorded · {c.status}</strong>
                <span>Case version {c.version}. Approver identities are not returned by this API; the server enforces distinct supervisors.</span></div></div>
            </section>
          </div>
          <div className="r8-case-col">
            <section className="r8-card r8-risk" aria-label="Risk indicators">
              <div className="r8-card-head"><h3 className="r8-eyebrow">RISK INDICATORS</h3>
                {c.risk && c.risk.scorePpm !== null && <span className={"r8-chip" + (c.risk.scorePpm >= c.risk.thresholdPpm ? " is-coral" : "")}>{c.risk.scorePpm >= c.risk.thresholdPpm ? "AT/ABOVE THRESHOLD" : "BELOW THRESHOLD"}</span>}</div>
              {c.risk ? <>
                <div className="r8-risk-row"><div className="r8-risk-top"><strong>Recorded model score</strong><b>{c.risk.scorePpm === null ? "Warmup" : <>{c.risk.scorePpm.toLocaleString("en-US")}<small> ppm</small></>}</b></div>
                  <RiskBar ppm={c.risk.scorePpm} threshold={c.risk.thresholdPpm} />
                  <p className="r8-risk-note">Recorded score: <strong>{c.risk.scorePpm ?? "Warmup"}</strong> ppm · pinned threshold {c.risk.thresholdPpm} ppm. Bar scale 0–1,000,000 ppm; a synthetic model score, not a probability of fraud.</p></div>
                <div className="r8-risk-row"><div className="r8-risk-top"><strong>Screening decision</strong><b className="is-word">{c.risk.reason}</b></div><p className="r8-risk-note">{c.risk.policy}</p></div>
                <dl className="r8-obs">{c.risk.observations.length === 0 ? <div><dt>Observations</dt><dd>None recorded (warmup screening)</dd></div> :
                  c.risk.observations.map(o => <div key={o.name}><dt>{o.name}</dt><dd className="r8-mono" title={o.value}>{o.value.length > 24 ? shortHash(o.value, 12, 6) : o.value}</dd></div>)}</dl>
              </> : <p className="r8-muted">No model screening evidence is recorded for this case.</p>}
            </section>
            <section className="r8-card" aria-label="Evidence and provenance">
              <div className="r8-card-head"><h3 className="r8-eyebrow">EVIDENCE &amp; PROVENANCE</h3><Icon name="shield" /></div>
              {c.risk ? <dl className="r8-obs">
                <div><dt>Model</dt><dd className="r8-mono" title={c.risk.modelId}>{shortHash(c.risk.modelId, 18, 6)}</dd></div>
                <div><dt>Evidence</dt><dd className="r8-mono" title={c.risk.evidenceSha256}>{shortHash(c.risk.evidenceSha256)} <span className="r8-tag-teal">SOURCE LINKED</span></dd></div>
              </dl> : <p className="r8-muted">Evidence digests are unavailable for this case.</p>}
            </section>
          </div>
        </div>
        <div className="r8-actionbar">
          <span className="r8-authority"><Icon name="lock" />{authority(c, role)}</span>
          {op.error && !decision && <p className="r8-error" role="alert">{op.error}</p>}
          {op.note && <p className="r8-ok" role="status">{op.note}</p>}
          <div className="r8-actionbar-buttons">
            {(["Reject", "Approve"] as const).map(action => <button key={action} className={"r8-btn " + (action === "Approve" ? "is-primary" : "is-danger")}
              disabled={!open || op.busy} onClick={e => { trigger.current = e.currentTarget; op.clear(); setDecision(action); }}>{action}{action === "Approve" && <Icon name="arrow" />}</button>)}
          </div>
        </div>
      </>}
    </div>
    {decision && c && <DecisionModal action={decision} c={c} p={p} busy={op.busy} error={op.error} onCancel={close} onConfirm={() => void commit(decision)} />}
  </div>;
}

function statusLine(c: Case) {
  if (c.status === "Open") return c.requiredApprovals > 1 ? `Settlement authorization pending · ${c.requiredApprovals} approvals required` : "Settlement authorization pending";
  if (c.status === "AwaitingSecondApproval") return `Awaiting approval ${c.approvalCount + 1} of ${c.requiredApprovals}`;
  return c.status === "Released" ? "Released for settlement" : "Rejected · terminal";
}
function authority(c: Case, role: string | null) {
  const who = role ? `Signed in as ${role}` : "Staff session";
  if (!isOpenCase(c)) return `${who} · case is ${c.status} and closed to further review`;
  if (c.requiredApprovals - c.approvalCount > 1) return `${who} · ${c.requiredApprovals} approvals required; a later approval must come from a different supervisor`;
  if (c.approvalCount > 0) return `${who} · final approval must come from a supervisor who has not already approved`;
  return `${who} · one approval releases this case · fresh MFA required`;
}

function Score({ risk }: { risk: Case["risk"] }) {
  if (!risk) return <span className="r8-score is-none">No score<small>MODEL SCORE</small></span>;
  const high = risk.scorePpm !== null && risk.scorePpm >= risk.thresholdPpm;
  return <span className={"r8-score" + (high ? " is-high" : "")}>
    {risk.scorePpm === null ? "Warmup" : <>{risk.scorePpm.toLocaleString("en-US")}<small> ppm</small></>}
    <RiskBar ppm={risk.scorePpm} threshold={risk.thresholdPpm} /><small>MODEL SCORE</small></span>;
}
function RiskBar({ ppm, threshold }: { ppm: number | null; threshold: number }) {
  const pct = (v: number) => Math.max(0, Math.min(100, (v / PPM_SCALE) * 100));
  return <span className={"r8-riskbar" + (ppm !== null && ppm >= threshold ? " is-high" : "")} aria-hidden="true">
    {ppm !== null && <i style={{ width: pct(ppm) + "%" }} />}<b style={{ left: pct(threshold) + "%" }} title={`Pinned threshold ${threshold} ppm`} /></span>;
}
function PathNode({ letter, name, role }: { letter: string; name: string; role: string }) {
  return <div className="r8-path-node"><span>{letter}</span><div><strong>{name}</strong><small>{role}</small></div></div>;
}
function Fact({ at, text, chip }: { at: string; text: string; chip: string }) {
  return <div className="r8-history-row"><time className="r8-mono">{at}</time><span>{text}</span><span className="r8-chip">{chip}</span></div>;
}

/** Consequence copy derives from the case and the single Approve/Reject operation; the server's four-eyes rules stay authoritative. */
function consequence(action: Action, c: Case) {
  const amount = money(c.amountMinor);
  if (action === "Reject") return {
    heading: `Reject ${amount}?`, lede: "This records a rejection. The case becomes Rejected, a terminal state: it cannot be approved later and this case will not release the payment for settlement.",
    title: "Immediate consequence · terminal rejection", body: "The decision is appended to the synthetic audit chain. The payment's subsequent ledger status is whatever the server commits; the queue refreshes after the request.",
    primary: "Reject payment" };
  const order = c.approvalCount + 1;
  if (order < c.requiredApprovals) return {
    heading: `Record approval ${order} of ${c.requiredApprovals} for ${amount}?`,
    lede: `This records your authorization only. The case moves to Awaiting second approval and the payment stays held.`,
    title: "Immediate consequence · additional authorization", body: `Reserved funds remain reserved; available balances do not change until a different supervisor records approval ${c.requiredApprovals}.`,
    primary: `Record approval ${order} of ${c.requiredApprovals}` };
  return {
    heading: `Release ${amount} for settlement?`,
    lede: c.requiredApprovals > 1 ? `This is the final required approval (${order} of ${c.requiredApprovals}). The server only accepts it from a supervisor who has not already approved.` : "This is the only approval this case requires.",
    title: "Immediate consequence · release and settlement attempt", body: "The case becomes Released and the server immediately attempts settlement of the held instruction. Reserved funds are expected to move; refreshed balances show the committed result.",
    primary: "Approve and release" };
}

function DecisionModal({ action, c, p, busy, error, onCancel, onConfirm }: { action: Action; c: Case; p: Payment | null; busy: boolean; error: string; onCancel: () => void; onConfirm: () => void }) {
  const box = useRef<HTMLDivElement>(null);
  const copy = consequence(action, c);
  const stillOpen = isOpenCase(c);
  useEffect(() => { box.current?.querySelector<HTMLElement>("[data-autofocus]")?.focus(); }, []);
  function onKey(e: KeyboardEvent) {
    if (e.key === "Escape" && !busy) { e.preventDefault(); onCancel(); return; }
    if (e.key !== "Tab" || !box.current) return;
    const items = [...box.current.querySelectorAll<HTMLElement>("button:not(:disabled), [href], [tabindex]:not([tabindex='-1'])")];
    if (items.length === 0) return;
    const first = items[0]!, last = items.at(-1)!;
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }
  return <div className="r8-scrim" onMouseDown={e => { if (e.target === e.currentTarget && !busy) onCancel(); }}>
    <div className={"r8-modal" + (action === "Reject" ? " is-reject" : "")} role="dialog" aria-modal="true" aria-labelledby="r8-modal-title" aria-describedby="r8-modal-lede" ref={box} onKeyDown={onKey}>
      <div className="r8-modal-top"><span className="r8-eyebrow">CONFIRM DECISION · {c.intentId}</span>
        <button className="r8-icon-btn is-outline" aria-label="Close" onClick={onCancel} disabled={busy}><Icon name="close" /></button></div>
      <h2 id="r8-modal-title">{copy.heading}</h2>
      <p id="r8-modal-lede">{copy.lede}</p>
      <dl className="r8-modal-rows">
        <div><dt>Originating bank</dt><dd>{p ? `${bankCode(p.sourceBank)} · ${bankAlias(p.sourceBank).name} (${p.sourceBank})` : "—"}</dd></div>
        <div><dt>Receiving bank</dt><dd>{p ? `${bankCode(p.destinationBank)} · ${bankAlias(p.destinationBank).name} (${p.destinationBank})` : "—"}</dd></div>
        <div><dt>Held for this instruction</dt><dd>{money(c.amountMinor)} USD</dd></div>
        <div><dt>Authority / status</dt><dd>{c.approvalCount} of {c.requiredApprovals} approvals · {c.status} · v{c.version}</dd></div>
      </dl>
      <div className={"r8-consequence" + (action === "Reject" ? " is-reject" : "")}><Icon name="warn" /><div><strong>{copy.title}</strong><p>{copy.body}</p></div></div>
      {!stillOpen && <p className="r8-error" role="alert">This case is now {c.status}; no further decision is possible.</p>}
      {error && <div className="r8-error" role="alert"><strong>{explainDecisionFailure(error)}</strong><br /><small>{error}</small></div>}
      <div className="r8-modal-actions">
        <button className="r8-btn" onClick={onCancel} disabled={busy} data-autofocus>Cancel</button>
        <button className={"r8-btn " + (action === "Reject" ? "is-danger-solid" : "is-primary")} onClick={onConfirm} disabled={busy || !stillOpen}>
          {busy ? "Committing…" : error ? "Retry · " + copy.primary : copy.primary}<Icon name="arrow" /></button>
      </div>
    </div>
  </div>;
}
