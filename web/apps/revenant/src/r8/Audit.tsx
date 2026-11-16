import { useMemo, useState } from "react";
import { shortHash, time } from "./format.ts";
import { Drawer, ErrorText, Facts, Icon, Table, unwrap, useLoad, type Api, type S, type Verified } from "./shared.tsx";

type Event = S["R8AuditEvent"];

function csvCell(v: string | number | null) {
  const s = v === null ? "" : String(v);
  return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export function Audit({ api, id, result }: { api: Api; id: string; result: Verified | null }) {
  const audit = useLoad(useMemo(() => () => unwrap(api.get("/revenant/r8/runs/{id}/audit", { path: { id } })), [api, id]));
  const value = audit.value;
  const [query, setQuery] = useState(""), [kind, setKind] = useState("all"), [actor, setActor] = useState("all");
  const events = value?.recentEvents ?? [];
  const kinds = [...new Set(events.map(e => e.event))].sort(), actors = [...new Set(events.map(e => e.actor))].sort();
  const rows = events.filter(e => (kind === "all" || e.event === kind) && (actor === "all" || e.actor === actor)
    && (!query.trim() || [String(e.sequence), e.event, e.actor, e.subject, e.evidenceSha256 ?? ""].some(v => v.toLowerCase().includes(query.trim().toLowerCase()))));
  const chainOk = value?.status === "Verified" && value.ledgerVerification === "Verified";
  const unchanged = Boolean(value && result && value.status === "Verified" && value.recordHash === result.sourceRecordHash && value.stateHash === result.sourceStateHash);

  function exportCsv(list: Event[]) {
    const header = ["sequence", "event", "actor", "subject", "subject_version", "evidence_sha256", "source_timeline"];
    const body = list.map(e => [e.sequence, e.event, e.actor, e.subject, e.subjectVersion, e.evidenceSha256, value!.timelineId].map(csvCell).join(","));
    const blob = new Blob([[`# Loaded rows only: ${list.length} of the most recent ${events.length} source audit events returned by the API`, header.join(","), ...body].join("\n")], { type: "text/csv" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob); a.download = `revenant-audit-${id.slice(0, 8)}-loaded-rows.csv`; a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  return <div className="r8-audit">
    <div className="r8-heading">
      <div><div className="r8-eyebrow">AUDIT · INTEGRITY RECORD</div><h1>Every decision is sequenced. Every branch has proof.</h1></div>
      <p className="r8-heading-note">Original · {!value ? (audit.error ? "verification unavailable" : "verifying…") : chainOk ? "● chain verified on load" : "● verification failed"}</p>
    </div>
    <div className="r8-audit-toolbar">
      <label className="r8-search"><Icon name="search" /><span className="r8-sr">Search audit events</span>
        <input type="search" placeholder="Search events, payment IDs, hashes…" value={query} onChange={e => setQuery(e.target.value)} /></label>
      <label className="r8-select"><span className="r8-sr">Event type</span><select aria-label="Event type" value={kind} onChange={e => setKind(e.target.value)}>
        <option value="all">All event types</option>{kinds.map(k => <option key={k}>{k}</option>)}</select></label>
      <label className="r8-select"><span className="r8-sr">Actor</span><select aria-label="Actor" value={actor} onChange={e => setActor(e.target.value)}>
        <option value="all">All actors</option>{actors.map(k => <option key={k}>{k}</option>)}</select></label>
      <span className="r8-chip">SOURCE · ORIGINAL ONLY</span>
      <button className="r8-btn is-tonal r8-push" disabled={!value || rows.length === 0} onClick={() => exportCsv(rows)}><Icon name="download" />Export loaded rows (CSV)</button>
    </div>
    <div className="r8-audit-grid">
      <section className="r8-card r8-audit-table" aria-label="Source audit events">
        <ErrorText error={audit.error} />
        {!value && !audit.error && <p className="r8-loading" role="status">Verifying source history…</p>}
        {value && <div className="r8-scroll" tabIndex={0}><table>
          <thead><tr><th scope="col">Seq #</th><th scope="col">Event</th><th scope="col">Actor</th><th scope="col">Subject / payment</th><th scope="col">Version</th><th scope="col">Source</th><th scope="col">Evidence hash</th></tr></thead>
          <tbody>{rows.map(e => <tr key={e.sequence}>
            <td className="r8-mono">{String(e.sequence).padStart(5, "0")}</td><td className="r8-strong">{e.event}</td><td>{e.actor}</td>
            <td className="r8-mono">{e.subject || "—"}</td><td className="r8-mono">{e.subjectVersion ?? "—"}</td>
            <td><span className="r8-chip">ORIGINAL</span></td><td className="r8-mono" title={e.evidenceSha256 ?? undefined}>{shortHash(e.evidenceSha256)}</td></tr>)}</tbody>
        </table>{rows.length === 0 && <p className="r8-queue-empty">{events.length === 0 ? "The source has not recorded audit events yet." : "No loaded event matches these filters."}</p>}</div>}
        {value && <p className="r8-table-foot">Showing {rows.length} of the {events.length} most recent source events returned · the API records sequence, not per-event timestamps · child events are not part of this source chain</p>}
      </section>
      <div className="r8-audit-side">
        <section className="r8-integrity" aria-label="Chain integrity">
          <div className="r8-card-head"><h2 className="r8-eyebrow">CHAIN INTEGRITY</h2><span className={"r8-chip is-dark" + (chainOk ? " is-ok" : "")}>● {value ? (chainOk ? "VERIFIED" : "CHECK FAILED") : "CHECKING"}</span></div>
          <p className="r8-integrity-title">{!value ? (audit.error ? "Verification unavailable." : "Verifying the source…") : chainOk ? "Source chain verified." : "Verification did not pass."}</p>
          <p className="r8-integrity-body">Each load re-verifies the source Timeline and the ledger audit chain on the server. A branch cannot change these values.</p>
          {value && <dl className="r8-integrity-rows"><div><dt>Original timeline</dt><dd>{value.status}</dd></div><div><dt>Ledger audit chain</dt><dd>{value.ledgerVerification}</dd></div>
            <div><dt>Last position</dt><dd className="r8-mono">{value.lastPosition}</dd></div></dl>}
          {value && <div className="r8-hashbox" title="Ledger audit head">{value.ledgerHeadHash}</div>}
          <div className="r8-integrity-actions">
            <button className="r8-btn is-ghost-dark" disabled={audit.loading} onClick={() => void audit.reload()}><Icon name="refresh" />{audit.loading ? "Verifying…" : "Verify again"}</button>
            {value && <Drawer summary="Source fingerprints" className="is-left"><Facts rows={[["Timeline", value.timelineId], ["State hash", value.stateHash], ["Record hash", value.recordHash],
              ["Manifest", value.manifestHash], ["Policy", value.policy], ["Model", value.model], ["Ledger audit head", value.ledgerHeadHash]]} /></Drawer>}
          </div>
        </section>
        <section className="r8-card r8-provenance" aria-label="Sandbox verification">
          <div className="r8-card-head"><h2 className="r8-eyebrow">SOURCE VS CHILD · PROVENANCE</h2><Icon name="fork" /></div>
          <p className="r8-prov-title">A branch cannot rewrite its source.</p>
          <div className="r8-prov-cols">
            <div className="is-source"><span className="r8-eyebrow">ORIGINAL RECORD</span><code title={value?.recordHash}>{shortHash(value?.recordHash)}</code></div>
            <div className="is-child"><span className="r8-eyebrow">CHILD RECORD</span><code title={result?.childRecordHash}>{result ? shortHash(result.childRecordHash) : "—"}</code></div>
          </div>
          {result ? <>
            {unchanged ? <p className="r8-prov-banner is-ok"><Icon name="shield" />Original history unchanged</p>
              : <p className="r8-prov-banner"><Icon name="warn" />{value ? "Source hashes do not match this comparison's recorded source." : "Waiting for source verification."}</p>}
            <Drawer summary="Child evidence" className="is-left">
              <Facts rows={[["Child Timeline", result.childTimelineId], ["Child record", result.childRecordHash], ["Child state", result.childStateHash],
                ["Result evidence", result.evidenceDigest], ["Policies", result.sourcePolicy + " → " + result.alternativePolicy],
                ["Cost assumptions", result.costPolicy], ["Continuation rule", result.continuationPolicy]]} />
              {(result.actions ?? []).length > 0 && <Table headers={["Intent", "Reviewer", "Time", "Action", "Disposition"]} rows={(result.actions ?? []).map(a => [a.intentId, a.actor, time(a.atMs), a.action, a.status + " · " + a.code])} />}
            </Drawer>
          </> : <p className="r8-prov-banner is-muted"><Icon name="warn" />No verified sandbox in view. Open a verified replay from Rewind to compare its child chain here.</p>}
        </section>
      </div>
    </div>
  </div>;
}
