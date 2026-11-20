// Pure view-model rules of the multiplayer journey (Spec 13, task 13.8). No React, no Pixi, no network: every function turns an authoritative
// room view or result (play/multiplayerWire.ts) into what a screen shows, so the wording and the edge cases are testable.
//
// Nothing here is a game rule. Who is host, who is ready, which slot a member got, who won and by how much are all read from the server's
// own fields; this file only decides how to say them. There are no display names in the contract: members are identified by their
// room-scoped opaque id, "You", and (once the server seals the round) their slot.
import type { MatchOutcomeWire, MatchPlayerResultWire, RoomMemberWire, RoomView } from "../play/multiplayerWire.ts";

/** Colour and shape of each ZERO-BASED server slot (characters.md, 13.0 audit A3). Display words only: the server has no such dictionary. */
export const SLOT_NAMES = ["orange circle", "pink square", "yellow diamond", "green squircle"] as const;
export const slotLabel = (slot: number): string => `P${slot + 1}`;
export const slotName = (slot: number): string => SLOT_NAMES[slot] ?? "player";

/** The server accepts any case, spaces and dashes, so the field only has to keep what it cannot ignore. */
export function normalizeCodeInput(raw: string): string {
  return raw.toUpperCase().replace(/[^0-9A-Z]/g, "");
}
/** Groups a code in fours for reading aloud ("ABCD-EFGH"). Display only: the unseparated code is what is copied and sent. */
export function formatRoomCode(code: string): string {
  const clean = normalizeCodeInput(code);
  return clean.length > 4 ? `${clean.slice(0, 4)}-${clean.slice(4)}` : clean;
}

export const MATCH_SETTLED = ["finalized", "aborted", "unverified"] as const;
/** The match has ended, durably or not: the arena is over and the result panel replaces the live HUD. */
export function matchHasEnded(matchStatus: string | null | undefined, finalFrame: boolean): boolean {
  return finalFrame || (matchStatus != null && matchStatus !== "running");
}
/** The server lets a competitor return to the lobby only after one of these (Rematch refuses while the result is still being recorded). */
export function matchIsSettled(matchStatus: string | null | undefined): boolean {
  return matchStatus != null && (MATCH_SETTLED as readonly string[]).includes(matchStatus);
}

export type RoomStage = "lobby" | "countdown" | "match" | "closed";
export function roomStage(view: RoomView): RoomStage {
  if (view.phase === "countdown") return "countdown";
  if (view.phase === "start_admitted") return "match";
  if (view.phase === "closed") return "closed";
  return "lobby";
}

// ---- lobby ------------------------------------------------------------------------------------------------------------------------
export interface LobbyCard {
  /** Position on the screen, left to right. Before the round is sealed this is join order; afterwards it IS the server slot. */
  readonly index: number;
  readonly member: RoomMemberWire | null;
  /** The server-sealed slot (zero-based), only once the countdown has sealed the round. Null means "not assigned yet", never a guess. */
  readonly slot: number | null;
  readonly isYou: boolean;
}

export const MAX_CARDS = 4;

export function lobbyCards(view: RoomView, youMemberId: string): LobbyCard[] {
  const competitors = view.members.filter((m) => m.role === "competitor");
  const total = Math.min(MAX_CARDS, Math.max(view.maxCompetitors ?? MAX_CARDS, 2));
  const cards: LobbyCard[] = [];
  const sealed = view.round?.slots ?? [];
  for (let index = 0; index < total; index++) {
    let member: RoomMemberWire | null = null;
    let slot: number | null = null;
    if (sealed.length > 0) {
      const seat = sealed.find((s) => s.slot === index);
      member = seat ? view.members.find((m) => m.memberId === seat.memberId) ?? null : null;
      slot = seat ? seat.slot : null;
    } else member = competitors[index] ?? null;
    cards.push({ index, member, slot, isYou: member?.memberId === youMemberId });
  }
  return cards;
}

export function spectatorCount(view: RoomView): number {
  return view.members.filter((m) => m.role === "spectator").length;
}

export interface StartAdvice { readonly allowed: boolean; readonly reason: string }
/** What the host's Start button should say. Advice only: the server re-checks every condition when the command arrives. */
export function startAdvice(view: RoomView): StartAdvice {
  if (view.phase !== "lobby") return { allowed: false, reason: "A countdown is already running." };
  const competitors = view.members.filter((m) => m.role === "competitor");
  if (competitors.length < 2) return { allowed: false, reason: "Waiting for a second player." };
  if (competitors.some((m) => !m.connected)) return { allowed: false, reason: "Someone in the room is disconnected." };
  if (competitors.some((m) => !m.ready)) return { allowed: false, reason: "Waiting for everyone to be ready." };
  return { allowed: true, reason: `${competitors.length} players ready.` };
}

const NOTICE_TEXT: Readonly<Record<string, string>> = {
  countdown_started: "Countdown started.",
  countdown_cancelled_by_host: "The host cancelled the countdown.",
  countdown_cancelled_unready: "The countdown stopped because someone is no longer ready.",
  countdown_cancelled_participant_lost: "The countdown stopped because a player left or disconnected.",
  countdown_cancelled_roster_changed: "The countdown stopped because the players changed.",
  start_unavailable: "The match could not start. Ready up and try again.",
  host_changed: "The host changed.",
  rematch_lobby: "Back in the lobby. Ready up for a new match.",
};
/** Only notices worth interrupting the lobby for; the rest (started, admitted, expired) are visible in the room itself. */
export function noticeText(notice: string | null | undefined): string | null {
  return notice ? NOTICE_TEXT[notice] ?? null : null;
}

/**
 * Seconds to show on the countdown. The server's `countdownRemainingMs` is authoritative; between two reads of the room the number only
 * counts down locally from that reading. Never below 0, and null when the room is not counting down.
 */
export function countdownSecondsLeft(view: RoomView, readAtMs: number, nowMs: number): number | null {
  if (view.phase !== "countdown") return null;
  const left = (view.countdownRemainingMs ?? 0) - Math.max(0, nowMs - readAtMs);
  return Math.max(0, Math.ceil(left / 1000));
}

// ---- results ----------------------------------------------------------------------------------------------------------------------
export interface ResultRow extends MatchPlayerResultWire {
  readonly isYou: boolean;
  readonly forfeited: boolean;
  /** More than one row holds this competitive placement (the server's competition ranking: 1, 1, 3). */
  readonly tied: boolean;
}
export type ResultStatus = "finalized" | "aborted" | "pending";
export interface ResultModel {
  readonly status: ResultStatus;
  readonly rows: readonly ResultRow[];
  /** Slots holding first place AMONG FINISHERS. Empty unless the result is finalized: no winner is ever named from anything else. */
  readonly winners: readonly number[];
  readonly headline: string;
  readonly sub: string | null;
}

export function ordinal(n: number): string {
  const tail = n % 100;
  if (tail >= 11 && tail <= 13) return `${n}th`;
  return `${n}${["th", "st", "nd", "rd"][n % 10] ?? "th"}`;
}

function joinSlots(slots: readonly number[]): string {
  const names = slots.map(slotLabel);
  return names.length <= 2 ? names.join(" and ") : `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
}

export function resultModel(outcome: MatchOutcomeWire, youMemberId: string): ResultModel {
  const status: ResultStatus = outcome.decision === "finalized" ? "finalized" : outcome.decision === "aborted" ? "aborted" : "pending";
  if (status !== "finalized") {
    return status === "aborted"
      ? { status, rows: [], winners: [], headline: "Match aborted", sub: `No winner and no placements.${outcome.abortReason ? ` Reason: ${outcome.abortReason}.` : ""}` }
      : { status, rows: [], winners: [], headline: "Result pending", sub: "The server hasn't recorded a final result yet." };
  }
  const placementCount = new Map<number, number>();
  for (const p of outcome.players) placementCount.set(p.competitivePlacement, (placementCount.get(p.competitivePlacement) ?? 0) + 1);
  const rows: ResultRow[] = [...outcome.players]
    .sort((a, b) => a.competitivePlacement - b.competitivePlacement || a.slot - b.slot)
    .map((p) => ({ ...p, isYou: p.memberId === youMemberId, forfeited: p.outcome === "forfeit", tied: (placementCount.get(p.competitivePlacement) ?? 0) > 1 }));
  const winners = rows.filter((r) => r.competitivePlacement === 1 && !r.forfeited).map((r) => r.slot);
  const forfeits = rows.filter((r) => r.forfeited).map((r) => r.slot);
  const forfeitNote = forfeits.length > 0 ? `${joinSlots(forfeits)} forfeited.` : null;
  if (winners.length === 0) return { status, rows, winners, headline: "No one finished", sub: forfeitNote ?? "No competitor reached the end of the round." };
  const topScore = rows.find((r) => r.slot === winners[0])?.totalScore ?? 0;
  if (winners.length > 1) {
    return { status, rows, winners, headline: "A tie for first", sub: [`${joinSlots(winners)} share first place on ${topScore}.`, forfeitNote].filter(Boolean).join(" ") };
  }
  const only = rows.find((r) => r.slot === winners[0])!;
  return { status, rows, winners, headline: only.isYou ? "Victory" : `${slotLabel(only.slot)} wins`, sub: [`${slotLabel(only.slot)} finished first on ${topScore}.`, forfeitNote].filter(Boolean).join(" ") };
}

/** Milliseconds to wait before each read of GetMatchResult; a pending or unavailable answer is retried this many times, then the player decides. */
export const RESULT_BACKOFF_MS: readonly number[] = [0, 600, 1200, 2000, 3000, 4500, 6000];
