// My Histories (Spec 13, task 13.9): the guest's own saved Originals and their saved futures (Challenge / Fork children), from the server.
// The list is only ever what the owner routes returned. Nothing is invented to fill space; a row's score appears only after the server
// verified that history (one rationed replay read per original). Task 13.10 made Replay and Compare real (they open the Sands of Time replay
// screen). Task 13.11 made Ghost Challenge real here (only for an original whose score the server has verified); the Fork point is chosen on the
// replay screen itself. A saved future has no Fork at all (no nested child forks).
import { useCallback, useEffect, useRef, useState } from "react";
import { BackLink, FrostPanel, GameButton, StatusPill } from "../ui/primitives.tsx";
import { SHAPE_BY_SLOT } from "../config/assetRegistry.ts";
import {
  AUTO_VERIFY_COUNT, cap, childRow, libraryCountText, mascotName, originalTitle, secondsAt, shortHash, shortId, verificationKey, verifySequentially, type Verification,
} from "./model.ts";
import {
  CHILDREN_PAGE, ensureGuestSession, getOriginal, httpFailureText, listChildren, listOriginals, ORIGINALS_LIST_CAP, SoloHttpError,
  type SoloChildWire, type SoloOriginalWire,
} from "./soloWire.ts";
import { useRecovery } from "./recovery.ts";
import { originalVersionKey, type ReplayTarget } from "../history/target.ts";
import { challengePlan, type RacePlan } from "../race/raceModel.ts";

/** What My Histories should show selected when it opens: the history the person just came back from (a saved future's tab, an original's row). */
export interface HistoriesFocus { readonly kind: "original" | "child"; readonly id: string }

type Tab = "originals" | "futures";
type Load<T> = { readonly kind: "loading" } | { readonly kind: "error"; readonly text: string } | { readonly kind: "ready"; readonly value: T };

const CHALLENGE_WHY = "Verify this original's score first (Verify score). A Ghost Challenge races only an original the server has confirmed.";

const failureOf = (e: unknown) => {
  const kind = e instanceof SoloHttpError ? e.kind : "offline";
  return { message: httpFailureText(e), retryable: kind === "busy" || kind === "unavailable" || kind === "offline", busy: kind === "busy" };
};

function HeadMark({ mascot }: { mascot: number }) {
  return <img className="gl-hist-head" src={SHAPE_BY_SLOT[mascot]?.file} alt="" width={34} height={34} data-slot={mascot} />;
}

export function HistoriesScreen({ onBack, onSolo, onReplay, onChallenge, focus = null }: {
  onBack: () => void; onSolo: () => void; onReplay: (target: ReplayTarget) => void; onChallenge: (plan: RacePlan) => void; focus?: HistoriesFocus | null;
}) {
  const [tab, setTab] = useState<Tab>(focus?.kind === "child" ? "futures" : "originals");
  const [originals, setOriginals] = useState<Load<SoloOriginalWire[]>>({ kind: "loading" });
  const [verify, setVerify] = useState<Readonly<Record<string, Verification>>>({});
  const [selected, setSelected] = useState<string | null>(focus?.kind === "original" ? focus.id : null);
  const [children, setChildren] = useState<Load<{ items: SoloChildWire[]; next: string | null }> | null>(null);
  const [moreBusy, setMoreBusy] = useState(false);
  const [selectedChild, setSelectedChild] = useState<string | null>(focus?.kind === "child" ? focus.id : null);
  const [reload, setReload] = useState(0);
  const abort = useRef<AbortController | null>(null);
  const { recovery, retry: recheck, dismiss } = useRecovery(() => setReload((n) => n + 1));

  // ---- originals: list, then a bounded sequential verification of the newest ones ---------------------------------------------------
  useEffect(() => {
    const controller = new AbortController();
    abort.current = controller;
    setOriginals({ kind: "loading" });
    void (async () => {
      try {
        await ensureGuestSession(controller.signal);
        const items = await listOriginals(controller.signal);
        if (controller.signal.aborted) return;
        setOriginals({ kind: "ready", value: items });
        setSelected((cur) => (cur && items.some((o) => o.id === cur) ? cur : items[0]?.id ?? null));
        await verifySequentially(
          items.slice(0, AUTO_VERIFY_COUNT),
          (id) => getOriginal(id, controller.signal),
          (o, state) => setVerify((v) => ({ ...v, [verificationKey(o)]: state })),
          () => controller.signal.aborted,
          failureOf,
        );
      } catch (error) {
        if (controller.signal.aborted) return;
        setOriginals({ kind: "error", text: error instanceof SoloHttpError ? httpFailureText(error) : error instanceof Error ? error.message : String(error) });
      }
    })();
    return () => controller.abort();
  }, [reload]);

  const verifyOne = useCallback(async (o: SoloOriginalWire) => {
    const signal = abort.current?.signal;
    setVerify((v) => ({ ...v, [verificationKey(o)]: { kind: "verifying" } }));
    try {
      const verified = await getOriginal(o.id, signal);
      if (signal?.aborted) return;
      setVerify((v) => ({ ...v, [verificationKey(o)]: { kind: "verified", original: verified } }));
    } catch (error) {
      if (signal?.aborted) return;
      const f = failureOf(error);
      setVerify((v) => ({ ...v, [verificationKey(o)]: { kind: "failed", message: f.message, retryable: f.retryable } }));
    }
  }, []);

  const select = useCallback((o: SoloOriginalWire) => {
    setSelected(o.id);
    const state = verify[verificationKey(o)];
    if (!state || state.kind === "unverified") void verifyOne(o);
  }, [verify, verifyOne]);

  // ---- saved futures, loaded the first time the tab opens, newest first, 10 at a time --------------------------------------------------
  const loadChildren = useCallback(async (after: string | null) => {
    const signal = abort.current?.signal;
    try {
      await ensureGuestSession(signal);
      const page = await listChildren(after, signal);
      if (signal?.aborted) return;
      setChildren((cur) => ({ kind: "ready", value: { items: [...(after && cur?.kind === "ready" ? cur.value.items : []), ...page.items], next: page.nextAfter } }));
      if (!after) setSelectedChild((c) => c ?? page.items[0]?.id ?? null);
    } catch (error) {
      if (signal?.aborted) return;
      const text = error instanceof SoloHttpError ? httpFailureText(error) : error instanceof Error ? error.message : String(error);
      setChildren((cur) => (after && cur?.kind === "ready" ? cur : { kind: "error", text }));
    }
  }, []);
  useEffect(() => {
    if (tab !== "futures" || children !== null) return;
    setChildren({ kind: "loading" });
    void loadChildren(null);
  }, [tab, children, loadChildren]);

  const list = originals.kind === "ready" ? originals.value : [];
  const chosen = list.find((o) => o.id === selected) ?? null;
  const futures = children?.kind === "ready" ? children.value : null;
  const chosenChild = futures?.items.find((c) => c.id === selectedChild) ?? null;
  const openSource = (id: string) => { setTab("originals"); const o = list.find((x) => x.id === id); if (o) select(o); };

  const originalsCount = originals.kind === "ready" ? libraryCountText(list.length, ORIGINALS_LIST_CAP) : null;
  const futuresCount = futures ? `${futures.items.length}${futures.next ? "+" : ""}` : null;

  return (
    <div className="gl-hist-wrap" data-testid="histories-screen">
      <div className="gl-study-top"><BackLink onClick={onBack}>Home</BackLink></div>
      <FrostPanel className="gl-hist" aria-label="My Histories">
        <header className="gl-hist-head-row">
          <h2>My Histories</h2>
          <p className="gl-mp-help">Private to you. Every score here was confirmed by the server.</p>
        </header>

        {recovery.kind !== "none" ? (
          <div className="gl-solo-note" role="status" data-testid="hist-recovery" data-kind={recovery.kind}>
            {recovery.kind === "interrupted" ? <span>Your last run was interrupted when the page closed. Unfinished runs can't be resumed, so it wasn't saved.</span> : null}
            {recovery.kind === "checking" ? <span>Checking whether your last run was saved…</span> : null}
            {recovery.kind === "saved" ? <span>Your last run was saved and verified. It's at the top of your Originals.</span> : null}
            {recovery.kind === "pending" ? <span>Your last run isn't confirmed as saved yet. {recovery.reason ?? ""}</span> : null}
            {recovery.kind === "failed" ? <span>Your last run could not be saved. {recovery.text}</span> : null}
            {recovery.kind === "pending" ? <button type="button" className="gl-mp-link" onClick={recheck}>Check again</button> : null}
            {recovery.kind !== "checking" ? <button type="button" className="gl-mp-link" onClick={dismiss}>Dismiss</button> : null}
          </div>
        ) : null}

        <div className="gl-mp-tabs gl-hist-tabs" role="tablist" aria-label="Library">
          <button type="button" role="tab" aria-selected={tab === "originals"} onClick={() => setTab("originals")} data-testid="hist-tab-originals">Originals{originalsCount ? <small> · {originalsCount}</small> : null}</button>
          <button type="button" role="tab" aria-selected={tab === "futures"} onClick={() => setTab("futures")} data-testid="hist-tab-futures">Saved Futures{futuresCount ? <small> · {futuresCount}</small> : null}</button>
        </div>

        {tab === "originals" ? (
          originals.kind === "loading" ? <p className="gl-mp-help" role="status" data-testid="hist-loading">Loading your histories…</p>
          : originals.kind === "error" ? (
            <div className="gl-hist-empty" role="alert" data-testid="hist-error">
              <p>{originals.text}</p>
              <GameButton variant="primary" onClick={() => setReload((n) => n + 1)}>Try again</GameButton>
            </div>
          ) : list.length === 0 ? (
            <div className="gl-hist-empty" data-testid="hist-empty">
              <p>Nothing recorded yet.</p>
              <p className="gl-mp-help">Play a Solo Run to make your first history. Only a finished, verified run appears here.</p>
              <GameButton variant="primary" onClick={onSolo} data-testid="hist-record">Record an Original</GameButton>
            </div>
          ) : (
            <div className="gl-hist-body">
              <ul className="gl-hist-list" aria-label="Originals" data-testid="hist-originals">
                {list.map((o) => {
                  const v = verify[verificationKey(o)];
                  return (
                    <li key={o.id}>
                      <button type="button" className="gl-hist-row" aria-pressed={selected === o.id} onClick={() => select(o)} data-testid="hist-row" data-id={o.id}>
                        <HeadMark mascot={o.mascotId} />
                        <span className="gl-hist-row-main"><b>{originalTitle(o)}</b><small>#{shortId(o.id)}</small></span>
                        <span className="gl-hist-row-score" data-state={v?.kind ?? "unverified"}>
                          {v?.kind === "verified" ? v.original.score ?? "–" : v?.kind === "verifying" ? "Verifying…" : v?.kind === "failed" ? "Needs attention" : "Not yet verified"}
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
              {chosen ? <OriginalDetail o={chosen} v={verify[verificationKey(chosen)]} onVerify={() => void verifyOne(chosen)} onSolo={onSolo} onReplay={() => onReplay({ kind: "original", id: chosen.id, original: chosen })}
                onChallenge={() => onChallenge(challengePlan({ originalId: chosen.id, mascotId: chosen.mascotId, finalStateHash: chosen.finalStateHash, versionKey: originalVersionKey(chosen) }))} /> : null}
            </div>
          )
        ) : children === null || children.kind === "loading" ? (
          <p className="gl-mp-help" role="status" data-testid="hist-loading">Loading your saved futures…</p>
        ) : children.kind === "error" ? (
          <div className="gl-hist-empty" role="alert" data-testid="hist-error">
            <p>{children.text}</p>
            <GameButton variant="primary" onClick={() => { setChildren({ kind: "loading" }); void loadChildren(null); }}>Try again</GameButton>
          </div>
        ) : children.value.items.length === 0 ? (
          <div className="gl-hist-empty" data-testid="hist-empty-futures">
            <p>No saved futures yet.</p>
            <p className="gl-mp-help">Ghost races and forks appear here after a verified finish.</p>
          </div>
        ) : (
          <div className="gl-hist-body">
            <ul className="gl-hist-list" aria-label="Saved futures" data-testid="hist-futures">
              {children.value.items.map((c) => {
                const r = childRow(c);
                return (
                  <li key={c.id}>
                    <button type="button" className="gl-hist-row" aria-pressed={selectedChild === c.id} onClick={() => setSelectedChild(c.id)} data-testid="hist-child-row" data-kind={c.kind} data-id={c.id}>
                      <HeadMark mascot={c.mascotId} />
                      <span className="gl-hist-row-main"><b>{r.title}</b><small>{r.detail}</small></span>
                      <span className="gl-hist-row-score" data-state={r.needsAttention ? "failed" : "verified"}>{r.needsAttention ? "Needs attention" : r.scoreText}</span>
                    </button>
                  </li>
                );
              })}
              {children.value.next ? (
                <li><button type="button" className="gl-mp-link" disabled={moreBusy} onClick={() => { setMoreBusy(true); void loadChildren(children.value.next).finally(() => setMoreBusy(false)); }} data-testid="hist-more">
                  {moreBusy ? "Loading…" : `Show ${CHILDREN_PAGE} older`}
                </button></li>
              ) : null}
            </ul>
            {chosenChild ? <ChildDetail c={chosenChild} sourceKnown={list.some((o) => o.id === chosenChild.sourceId)} onSource={() => openSource(chosenChild.sourceId)} onReplay={() => onReplay({ kind: "child", id: chosenChild.id, child: chosenChild, source: list.find((o) => o.id === chosenChild.sourceId) ?? null })} /> : null}
          </div>
        )}
      </FrostPanel>
    </div>
  );
}

function Identity({ rows }: { rows: readonly (readonly [string, string])[] }) {
  return (
    <details className="gl-hist-identity" data-testid="hist-identity">
      <summary>Identity</summary>
      <dl>{rows.map(([k, v]) => (<div key={k}><dt>{k}</dt><dd>{v}</dd></div>))}</dl>
    </details>
  );
}

function OriginalDetail({ o, v, onVerify, onSolo, onReplay, onChallenge }: { o: SoloOriginalWire; v: Verification | undefined; onVerify: () => void; onSolo: () => void; onReplay: () => void; onChallenge: () => void }) {
  const verified = v?.kind === "verified" ? v.original : null;
  return (
    <section className="gl-hist-detail" aria-label="Selected original" data-testid="hist-detail">
      <p className="gl-eyebrow">Original</p>
      <h3>{originalTitle(o)}</h3>
      <p className="gl-mp-help">#{shortId(o.id)} · {secondsAt(o.finalTick)} s · {cap(mascotName(o.mascotId))}</p>
      <div className="gl-solo-score" data-testid="hist-score" data-verified={verified ? "1" : "0"}>
        <span className="gl-solo-score-n">{verified?.score ?? "–"}</span>
        <span className="gl-solo-score-cap">
          {verified ? "Verified score" : v?.kind === "verifying" ? "Verifying…" : v?.kind === "failed" ? v.message : "Not yet verified"}
        </span>
      </div>
      {v?.kind === "failed" && v.retryable ? <GameButton onClick={onVerify} data-testid="hist-verify">Verify again</GameButton> : null}
      {!v || v.kind === "unverified" ? <GameButton onClick={onVerify} data-testid="hist-verify">Verify score</GameButton> : null}
      {verified ? <StatusPill tone="ok">Saved · verified</StatusPill> : null}
      <div className="gl-hist-actions">
        <GameButton variant="primary" onClick={onReplay} data-testid="hist-replay">Replay &amp; choose a fork point</GameButton>
        <GameButton onClick={onChallenge} data-testid="hist-challenge" {...(verified ? {} : { unavailable: CHALLENGE_WHY })}>Ghost Challenge</GameButton>
        <GameButton onClick={onSolo} data-testid="hist-another">Record another</GameButton>
      </div>
      <p className="gl-mp-help">Ghost Challenge races your whole original from T0. To fork a chosen moment, open Replay, pick the tick, then Fork here.</p>
      <Identity rows={[
        ["Rules", `${o.rulesVersion} · ${o.layoutVersion}`],
        ["Domain", `${o.domainId} · reducer ${o.reducerVersion}`],
        ["Schemas", `input ${o.inputSchemaVersion} · state ${o.stateSchemaVersion} · context ${o.contextSchemaVersion}`],
        ["Final tick", String(o.finalTick)],
        ["Final state", shortHash(o.finalStateHash)],
        ["Last record", shortHash(o.lastRecordHash)],
      ]} />
    </section>
  );
}

function ChildDetail({ c, sourceKnown, onSource, onReplay }: { c: SoloChildWire; sourceKnown: boolean; onSource: () => void; onReplay: () => void }) {
  const r = childRow(c);
  return (
    <section className="gl-hist-detail" aria-label="Selected saved future" data-testid="hist-child-detail">
      <p className="gl-eyebrow">{c.kind === "Fork" ? "Fork" : "Ghost challenge"}</p>
      <h3>{r.title}</h3>
      <p className="gl-mp-help">{r.detail} · source original #{shortId(c.sourceId)}</p>
      <div className="gl-solo-score" data-verified={r.needsAttention ? "0" : "1"}>
        <span className="gl-solo-score-n">{r.scoreText}</span>
        <span className="gl-solo-score-cap">{r.needsAttention ? "The server could not verify this future" : "Verified score"}</span>
      </div>
      <div className="gl-hist-actions">
        <GameButton variant="primary" onClick={onReplay} data-testid="hist-child-replay">Replay &amp; compare with the original</GameButton>
        {sourceKnown ? <GameButton onClick={onSource} data-testid="hist-source">Return to source</GameButton> : <GameButton unavailable="The source original isn't in your loaded Originals.">Return to source</GameButton>}
      </div>
      <p className="gl-mp-help">A saved future can't be forked again.</p>
      <Identity rows={[
        ["Rules", `${c.versions.rulesVersion} · ${c.versions.layoutVersion}`],
        ["Kind", c.kind + (c.kind === "Fork" ? ` at tick ${c.forkTick}` : "")],
        ["Final tick", String(c.finalTick)],
        ["Final state", shortHash(c.finalStateHash)],
        ["Source", c.sourceId],
      ]} />
    </section>
  );
}
