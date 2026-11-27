// Presentational pieces of the multiplayer journey (Spec 13, task 13.8): setup, lobby and results. They hold no network and no game rule; the
// screen hands them what the server said and the callbacks that ask the server for something. Same FrostPanel / GameButton / StatusPill as
// every other screen: no new skin.
import { useState, type FormEvent } from "react";
import { BackLink, FrostPanel, GameButton, StatusPill } from "../ui/primitives.tsx";
import { SHAPE_BY_SLOT } from "../config/assetRegistry.ts";
import { LOBBY_CARD_X } from "./LobbyHeads.ts";
import { formatRoomCode, ordinal, slotLabel, slotName, spectatorCount, type LobbyCard, type ResultModel, type ResultRow, type StartAdvice } from "./model.ts";
import type { JoinedRoom } from "../play/multiplayerWire.ts";

const worldPct = (x: number) => `${(x / 1600) * 100}%`;

// ---- setup: Create / Join (one frost dialog) -------------------------------------------------------------------------------------------
export type SetupTab = "create" | "join";
export interface SetupProps {
  readonly tab: SetupTab; readonly onTab: (tab: SetupTab) => void;
  readonly code: string; readonly onCode: (code: string) => void;
  readonly spectator: boolean; readonly onSpectator: (on: boolean) => void;
  readonly pending: boolean; readonly ready: boolean; readonly loadError: string | null; readonly error: string;
  readonly onSubmit: () => void; readonly onBack: () => void;
}
export function SetupPanel(p: SetupProps) {
  const submit = (e: FormEvent) => { e.preventDefault(); if (!p.pending && p.ready && (p.tab === "create" || p.code.length > 0)) p.onSubmit(); };
  const action = p.tab === "create" ? "Create room" : p.spectator ? "Watch room" : "Join room";
  const blocked = p.pending ? "Working…" : !p.ready ? "Loading the characters…" : p.tab === "join" && p.code.length === 0 ? "Enter a room code" : undefined;
  return (
    <div className="gl-mp-setup-wrap" data-testid="mp-setup">
      <div className="gl-study-top"><BackLink onClick={p.onBack}>Home</BackLink></div>
      <FrostPanel className="gl-mp-setup">
        <h2>Multiplayer</h2>
        <div className="gl-mp-tabs" role="tablist" aria-label="Room">
          <button type="button" role="tab" aria-selected={p.tab === "create"} onClick={() => p.onTab("create")} data-testid="mp-tab-create">Create room</button>
          <button type="button" role="tab" aria-selected={p.tab === "join"} onClick={() => p.onTab("join")} data-testid="mp-tab-join">Join room</button>
        </div>
        <form onSubmit={submit} className="gl-mp-form">
          {p.tab === "create" ? (
            <p className="gl-mp-help">Open a private room and share its code. Two to four players. Rooms are not listed anywhere, so only people you give the code to can find it.</p>
          ) : (
            <>
              <label className="gl-mp-field">
                <span>Room code</span>
                <input value={p.code} onChange={(e) => p.onCode(e.currentTarget.value)} autoComplete="off" autoCapitalize="characters" spellCheck={false} maxLength={24}
                  placeholder="ABCD-EFGH" data-testid="mp-code-input" aria-describedby="mp-code-help" />
              </label>
              <label className="gl-mp-check">
                <input type="checkbox" checked={p.spectator} onChange={(e) => p.onSpectator(e.currentTarget.checked)} data-testid="mp-spectator" />
                <span>Join as a spectator (watch only)</span>
              </label>
              <p className="gl-mp-help" id="mp-code-help">Letters and numbers only; dashes and spaces are ignored.</p>
            </>
          )}
          {p.loadError ? <p className="gl-mp-error" role="alert">Characters could not load: {p.loadError}</p> : null}
          {p.error ? <p className="gl-mp-error" role="alert" data-testid="mp-error">{p.error}</p> : null}
          <GameButton variant="primary" type="submit" data-testid="mp-submit" {...(blocked ? { unavailable: blocked } : {})}>{p.pending ? "Connecting…" : action}</GameButton>
        </form>
      </FrostPanel>
    </div>
  );
}

// ---- lobby ----------------------------------------------------------------------------------------------------------------------
export interface LobbyProps {
  readonly room: JoinedRoom; readonly cards: readonly LobbyCard[]; readonly sealed: boolean;
  readonly countdownLeft: number | null; readonly notice: string | null; readonly error: string;
  readonly busy: string | null; readonly advice: StartAdvice; readonly isHost: boolean; readonly youReady: boolean; readonly isSpectator: boolean;
  readonly connectionNote: string | null;
  readonly onReady: () => void; readonly onStart: () => void; readonly onCancel: () => void; readonly onLeave: () => void;
}

export function CopyCode({ code }: { code: string }) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");
  const copy = () => {
    const write = navigator.clipboard?.writeText(code);
    if (!write) { setState("failed"); return; }
    void write.then(() => setState("copied"), () => setState("failed"));
  };
  return (
    <span className="gl-mp-copy">
      <button type="button" onClick={copy} data-testid="mp-copy">Copy code</button>
      <span role="status" aria-live="polite">{state === "copied" ? "Copied" : state === "failed" ? "Couldn't copy. Select the code instead." : ""}</span>
    </span>
  );
}

export function LobbyView(p: LobbyProps) {
  const counting = p.countdownLeft !== null;
  const watchers = spectatorCount(p.room.view);
  const youCard = p.cards.find((c) => c.isYou);
  return (
    <div className="gl-mp-lobby" data-testid="mp-lobby" data-counting={counting ? "1" : "0"}>
      <div className="gl-study-top">
        <BackLink onClick={p.onLeave}>Leave room</BackLink>
        <StatusPill tone={p.connectionNote ? "warn" : "ok"} role="status" data-testid="mp-connection">{p.connectionNote ?? (p.isSpectator ? "Connected · spectating" : "Connected")}</StatusPill>
      </div>

      <header className="gl-mp-room">
        <p className="gl-eyebrow">Room</p>
        <p className="gl-mp-code" data-testid="room-code" aria-label={`Room code ${p.room.roomCode.split("").join(" ")}`}>{formatRoomCode(p.room.roomCode)}</p>
        <CopyCode code={p.room.roomCode} />
        <p className="gl-mp-sub" aria-live="polite" data-testid="mp-sub">
          {counting ? <><span className="gl-mp-count" data-testid="mp-countdown">{p.countdownLeft > 0 ? p.countdownLeft : "…"}</span> Match starts</> : p.notice ?? "Share the code. Characters are assigned when the countdown starts."}
        </p>
      </header>

      <ol className="gl-mp-cards" aria-label="Players">
        {p.cards.map((c) => {
          const x = worldPct(LOBBY_CARD_X[c.index] ?? 800);
          if (!c.member) {
            return (
              <li key={c.index} className="gl-mp-card gl-mp-card--empty" style={{ left: x }} data-testid="mp-card" data-state="empty">
                <span className="gl-mp-ring" aria-hidden="true" />
                <span className="gl-mp-card-title">Open seat</span>
                <span className="gl-mp-card-sub">{p.sealed ? "Not in this match" : "Waiting for a player"}</span>
              </li>
            );
          }
          const m = c.member;
          return (
            <li key={c.index} className="gl-mp-card" style={{ left: x }} data-testid="mp-card" data-state={m.ready ? "ready" : "waiting"} data-you={c.isYou ? "1" : "0"} data-slot={c.slot ?? ""}>
              {c.slot === null ? <span className="gl-mp-ring gl-mp-ring--joined" aria-hidden="true">?</span> : null}
              <span className="gl-mp-card-title">{c.slot !== null ? `${slotLabel(c.slot)} · ${slotName(c.slot)}` : c.isYou ? "You" : "Player"}</span>
              <span className="gl-mp-card-tags">
                {c.slot !== null && c.isYou ? <b>You</b> : null}
                {m.isHost ? <b>Host</b> : null}
                {!m.connected ? <b data-tone="warn">Disconnected</b> : null}
              </span>
              <span className="gl-mp-chip" data-ready={m.ready ? "1" : "0"}>{m.ready ? "Ready" : "Not ready"}</span>
            </li>
          );
        })}
      </ol>

      <div className="gl-mp-actions">
        {p.isSpectator ? (
          <p className="gl-mp-help" data-testid="mp-spectator-note">You're watching. The match appears here when the host starts it; spectators have no controls.</p>
        ) : (
          <>
            <div className="gl-mp-buttons">
              <GameButton variant={p.youReady ? "quiet" : "primary"} onClick={p.onReady} data-testid="mp-ready"
                {...(p.busy === "ready" ? { unavailable: "Working…" } : youCard?.member && !youCard.member.connected ? { unavailable: "Reconnecting…" } : {})}>
                {p.youReady ? "Not ready" : "Ready"}
              </GameButton>
              {p.isHost ? (
                counting ? (
                  <GameButton onClick={p.onCancel} data-testid="mp-cancel" {...(p.busy === "cancel" ? { unavailable: "Working…" } : {})}>Cancel countdown</GameButton>
                ) : (
                  <GameButton variant="primary" onClick={p.onStart} data-testid="mp-start"
                    {...(p.busy === "start" ? { unavailable: "Working…" } : p.advice.allowed ? {} : { unavailable: p.advice.reason })}>Start countdown</GameButton>
                )
              ) : null}
            </div>
            <p className="gl-mp-help" data-testid="mp-advice">
              {p.isHost ? (counting ? "Only the server's clock starts the match. Un-readying also cancels it." : p.advice.reason) : counting ? "Un-readying cancels the countdown." : "The host starts the match once everyone is ready."}
            </p>
          </>
        )}
        {watchers > 0 ? <p className="gl-mp-help">{watchers === 1 ? "1 person is watching." : `${watchers} people are watching.`}</p> : null}
        {p.error ? <p className="gl-mp-error" role="alert" data-testid="mp-error">{p.error}</p> : null}
      </div>
    </div>
  );
}

// ---- results --------------------------------------------------------------------------------------------------------------------
export interface ResultsProps {
  readonly state: { readonly kind: "loading" | "ready" | "denied" | "stalled"; readonly model: ResultModel | null; readonly text: string; readonly busy: boolean };
  readonly canRematch: boolean; readonly rematchWhy: string | null; readonly rematchBusy: boolean; readonly error: string;
  readonly spectator: boolean;
  readonly onRematch: () => void; readonly onHome: () => void; readonly onRetry: () => void; readonly onViewArena: () => void;
}

function Breakdown({ row }: { row: ResultRow }) {
  return (
    <p className="gl-mp-break">
      <span>Movement <b>{row.movementPoints}</b></span>
      <span>Candles <b>{row.pickupPoints}</b></span>
      <span>Penalties <b>{row.penaltyPoints > 0 ? `−${row.penaltyPoints}` : 0}</b></span>
      <span>{row.pickups} {row.pickups === 1 ? "candle" : "candles"} · {row.bursts} {row.bursts === 1 ? "Burst" : "Bursts"} · {row.burstHits} Burst {row.burstHits === 1 ? "hit" : "hits"} · hit by Burst {row.timesHitByBurst}× · {row.contactDrainsSuffered} contact {row.contactDrainsSuffered === 1 ? "drain" : "drains"}</span>
    </p>
  );
}

export function ResultsPanel(p: ResultsProps) {
  const [open, setOpen] = useState(false);
  const model = p.state.model;
  const finalized = model?.status === "finalized";
  const headline = p.state.kind === "loading" ? "Time" : p.state.kind === "denied" ? "Result unavailable" : p.state.kind === "stalled" && !model ? "Result not available yet" : model?.headline ?? "Time";
  const sub = p.state.kind === "loading" ? "Waiting for the server to confirm the result…" : p.state.kind === "ready" ? model?.sub ?? null : p.state.text;
  const canCheck = (p.state.kind === "stalled" || (p.state.kind === "ready" && model?.status === "pending")) && !p.state.busy;
  return (
    <FrostPanel className="gl-mp-results" role="region" aria-label="Match results" data-testid="mp-results" data-status={model?.status ?? p.state.kind}>
      <h2 data-testid="mp-headline">{headline}</h2>
      {sub ? <p className="gl-mp-results-sub" role="status" data-testid="mp-result-sub">{sub}</p> : null}
      {finalized && model ? (
        <>
          <ol className="gl-mp-rows" data-testid="mp-rows">
            {model.rows.map((r) => (
              <li key={r.slot} data-you={r.isYou ? "1" : "0"} data-winner={model.winners.includes(r.slot) ? "1" : "0"} data-slot={r.slot}>
                <div className="gl-mp-row">
                  <img src={SHAPE_BY_SLOT[r.slot]?.file} alt="" width={30} height={30} data-slot={r.slot} />
                  <span className="gl-mp-row-who">
                    <b>{slotLabel(r.slot)}</b> {slotName(r.slot)}
                    {r.isYou ? <em>You</em> : null}
                    {r.forfeited ? <em data-tone="warn">Forfeited at {(r.forfeitTick / 30).toFixed(0)} s</em> : null}
                  </span>
                  <span className="gl-mp-row-place">{r.tied ? "Tied " : ""}{ordinal(r.competitivePlacement)}</span>
                  <span className="gl-mp-row-score" data-testid="mp-score">{r.totalScore}</span>
                </div>
                {open ? <Breakdown row={r} /> : null}
              </li>
            ))}
          </ol>
          <button type="button" className="gl-mp-link" aria-expanded={open} onClick={() => setOpen(!open)} data-testid="mp-breakdown">{open ? "Hide breakdown" : "Show breakdown"}</button>
        </>
      ) : null}
      {p.error ? <p className="gl-mp-error" role="alert" data-testid="mp-error">{p.error}</p> : null}
      <div className="gl-mp-results-actions">
        {canCheck ? <GameButton onClick={p.onRetry} data-testid="mp-retry">Check again</GameButton> : null}
        {p.spectator ? null : (
          <GameButton variant="primary" onClick={p.onRematch} data-testid="mp-rematch"
            {...(p.rematchBusy ? { unavailable: "Working…" } : p.canRematch ? {} : { unavailable: p.rematchWhy ?? "Not available yet" })}>Rematch</GameButton>
        )}
        <GameButton onClick={p.onHome} data-testid="mp-home">Home</GameButton>
      </div>
      <div className="gl-mp-results-foot">
        <button type="button" className="gl-mp-link" onClick={p.onViewArena} data-testid="mp-view-arena">View the arena</button>
        {p.spectator ? <span>Only a player can start a rematch; the room returns here when one does.</span> : p.canRematch ? <span>Rematch returns everyone to the lobby for a brand-new match.</span> : null}
      </div>
    </FrostPanel>
  );
}
