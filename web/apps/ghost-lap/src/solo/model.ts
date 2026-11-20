// Pure view-model rules of the Solo journey and My Histories (Spec 13, task 13.9). No React, no Pixi, no network.
// Nothing here is a game rule: the server's own save status, score and hashes are only worded here. A save is "saved" only when the
// server answered Saved WITH an original; every other answer is pending, failed or interrupted, never a softened success.
import { SLOT_NAMES } from "../mp/model.ts";
import type { SoloChildWire, SoloOriginalWire, SoloSaveWire } from "./soloWire.ts";

export const MASCOT_COUNT = 4;
export const mascotName = (id: number): string => SLOT_NAMES[id] ?? "mascot";
export const cap = (s: string): string => (s.length === 0 ? s : s[0]!.toUpperCase() + s.slice(1));

/** `^[A-Za-z0-9._-]{1,100}$`: the server allows 1-100 of letters, digits, '-', '_' and '.'. A UUID suffix keeps keys unique per guest and run. */
export const OPERATION_KEY = /^[A-Za-z0-9._-]{1,100}$/;
export function newOperationKey(random: () => string = () => crypto.randomUUID()): string {
  const key = `solo-${random()}`;
  if (!OPERATION_KEY.test(key)) throw new Error("Generated operation key is not valid");
  return key;
}

// ---- the run that is (or was) in flight: the only thing kept in this browser --------------------------------------------------------
/**
 * A durable claim on a save, so a refresh after the last tick can still ask the server for the result of THAT run by its key. It is never a
 * saved run or a score. `ended` records whether a terminal frame or notification arrived before the page closed.
 */
export interface PendingRun { readonly key: string; readonly mascotId: number; readonly startedAtMs: number; readonly ended: "playing" | "final" }
export const PENDING_STORAGE_KEY = "ghostlap.solo.pending";
export const MASCOT_STORAGE_KEY = "ghostlap.solo.mascot";

type Store = Pick<Storage, "getItem" | "setItem" | "removeItem">;
const browserStore = (): Store | null => { try { return typeof localStorage === "undefined" ? null : localStorage; } catch { return null; } };

export function readPending(store: Store | null = browserStore()): PendingRun | null {
  if (!store) return null;
  try {
    const raw = store.getItem(PENDING_STORAGE_KEY);
    if (!raw) return null;
    const v = JSON.parse(raw) as Partial<PendingRun>;
    if (typeof v.key !== "string" || !OPERATION_KEY.test(v.key) || typeof v.mascotId !== "number" || (v.ended !== "playing" && v.ended !== "final") || typeof v.startedAtMs !== "number") return null;
    return { key: v.key, mascotId: v.mascotId, startedAtMs: v.startedAtMs, ended: v.ended };
  } catch { return null; }
}
export function writePending(run: PendingRun, store: Store | null = browserStore()): void { try { store?.setItem(PENDING_STORAGE_KEY, JSON.stringify(run)); } catch { /* storage unavailable: recovery by key is simply not offered */ } }
export function clearPending(store: Store | null = browserStore()): void { try { store?.removeItem(PENDING_STORAGE_KEY); } catch { /* ignore */ } }
export function readMascot(store: Store | null = browserStore()): number {
  try { const n = Number(store?.getItem(MASCOT_STORAGE_KEY)); return Number.isInteger(n) && n >= 0 && n < MASCOT_COUNT ? n : 0; } catch { return 0; }
}
export function writeMascot(id: number, store: Store | null = browserStore()): void { try { store?.setItem(MASCOT_STORAGE_KEY, String(id)); } catch { /* ignore */ } }

// ---- save outcome ---------------------------------------------------------------------------------------------------------------------
export type SaveState =
  | { readonly kind: "saving"; readonly attempt: number }
  /** The server said SavePending on every automatic try (or the call could not reach it). The run is not lost: Retry asks again with the same key. */
  | { readonly kind: "pending"; readonly reason: string | null }
  | { readonly kind: "saved"; readonly original: SoloOriginalWire }
  | { readonly kind: "failed"; readonly code: string; readonly retryable: boolean }
  | { readonly kind: "interrupted" };

/** Automatic same-key tries after the first (the server itself already retries its own save three times). Then the user decides. */
export const AUTO_RETRY_DELAYS_MS: readonly number[] = [1000, 2000, 3000, 5000];
const RETRYABLE = new Set(["unavailable", "server_busy", "save_failed", "error"]);
const NOT_A_RUN = new Set(["not_found", "not_complete", "interrupted"]);

/** Turns the server's `SoloSaveView` into what the player sees. `attempt` counts finished tries (1 = the first answer). */
export function interpretSave(view: SoloSaveWire, attempt: number): SaveState {
  if (view.status === "Saved") {
    return view.original?.finalTick === 2700 && Number.isSafeInteger(view.original.score)
      ? { kind: "saved", original: view.original }
      : { kind: "failed", code: "save_unconfirmed", retryable: true };
  }
  if (view.status === "SavePending") return attempt <= AUTO_RETRY_DELAYS_MS.length ? { kind: "saving", attempt } : { kind: "pending", reason: view.code ? saveFailureText(view.code) : null };
  const code = view.code ?? "save_failed";
  if (NOT_A_RUN.has(code)) return { kind: "interrupted" };
  return { kind: "failed", code, retryable: RETRYABLE.has(code) };
}
/** A hub-level reply (`{ ok:false, code }`) or a thrown connection error while asking for the save. */
export function interpretSaveError(code: string | null, text: string | null): SaveState {
  if (code && NOT_A_RUN.has(code)) return { kind: "interrupted" };
  return { kind: "pending", reason: text ?? code };
}

export const SAVE_FAILURE_TEXT: Readonly<Record<string, string>> = {
  save_unconfirmed: "The server did not confirm a verified original, so nothing is shown as saved.",
  save_failed: "The server could not finish saving this run.",
  verification_failed: "The server could not verify the recording, so it was not kept as an original.",
  corrupt_history: "The recording failed its integrity check, so it was not kept as an original.",
  version_unsupported: "The recording uses rules this server doesn't support.",
  conflict: "This key already belongs to a different run.",
  forbidden: "The server refused this session. Reload the page and start again.",
  invalid_request: "The server refused the save request.",
  unavailable: "The game server is unavailable right now.",
  server_busy: "The server is busy with other runs right now.",
};
export const saveFailureText = (code: string): string => SAVE_FAILURE_TEXT[code] ?? `The server answered "${code}".`;

// ---- run vocabulary ------------------------------------------------------------------------------------------------------------------
export const secondsAt = (tick: number): string => (tick / 30).toFixed(1);
export const shortId = (id: string): string => id.replace(/-/g, "").slice(0, 8);
export const shortHash = (hash: string): string => (hash.length > 16 ? `${hash.slice(0, 8)}…${hash.slice(-6)}` : hash);

export function originalTitle(o: Pick<SoloOriginalWire, "mascotId">): string { return `${cap(mascotName(o.mascotId))} original`; }

// ---- library ---------------------------------------------------------------------------------------------------------------------------
/** What the library knows about one original's verified score. The list never carries it; one rationed read per original does. */
export type Verification =
  | { readonly kind: "unverified" }
  | { readonly kind: "verifying" }
  | { readonly kind: "verified"; readonly original: SoloOriginalWire }
  | { readonly kind: "failed"; readonly message: string; readonly retryable: boolean };

export const verificationKey = (o: Pick<SoloOriginalWire, "id" | "finalStateHash">): string => `${o.id}:${o.finalStateHash}`;
/** How many of the newest originals are verified in the background on open. The rest verify when selected (each is a full replay on the server). */
export const AUTO_VERIFY_COUNT = 5;

/**
 * Verifies originals one at a time (concurrency 1, so the library never takes more than one of the server's four replay slots), newest first.
 * A busy answer (429) stops the sweep: the remaining rows stay "unverified" and verify when opened. Pure over an injected reader.
 */
export async function verifySequentially(
  originals: readonly SoloOriginalWire[],
  read: (id: string) => Promise<SoloOriginalWire>,
  report: (original: SoloOriginalWire, state: Verification) => void,
  shouldStop: () => boolean,
  failureOf: (error: unknown) => { message: string; retryable: boolean; busy: boolean },
): Promise<void> {
  for (const o of originals) {
    if (shouldStop()) return;
    report(o, { kind: "verifying" });
    try {
      const verified = await read(o.id);
      if (shouldStop()) return;
      report(o, { kind: "verified", original: verified });
    } catch (error) {
      if (shouldStop()) return;
      const f = failureOf(error);
      report(o, f.busy ? { kind: "unverified" } : { kind: "failed", message: f.message, retryable: f.retryable });
      if (f.busy) return;
    }
  }
}

export interface ChildRow {
  readonly child: SoloChildWire;
  readonly title: string;
  readonly detail: string;
  readonly scoreText: string;
  readonly needsAttention: boolean;
}
/** A saved future's row text from real fields only. An unverified child shows no score (the server only supplies one when verified). */
export function childRow(c: SoloChildWire): ChildRow {
  const fork = c.kind === "Fork";
  return {
    child: c,
    title: `${fork ? "Fork" : "Ghost challenge"} · ${mascotName(c.mascotId)}`,
    detail: fork ? `Forked at tick ${c.forkTick} (${secondsAt(c.forkTick)} s)` : "Raced the whole original from the start",
    scoreText: c.verified && c.score !== null ? String(c.score) : "–",
    needsAttention: !c.verified || c.status !== "Completed",
  };
}

/** The Originals tab shows at most the server's cap; saying so keeps "100" from posing as a total. */
export const libraryCountText = (n: number, cap: number): string => (n >= cap ? `newest ${cap}` : String(n));
