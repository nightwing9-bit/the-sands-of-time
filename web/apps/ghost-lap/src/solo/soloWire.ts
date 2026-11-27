// Wire shapes and HTTP reads of the production Solo surface (Spec 13, task 13.9).
// Checked against GhostLap.Contracts/Solo/GhostLapSoloContracts.cs (hub /hubs/ghostlap-solo and the owner library routes under
// /api/ghostlap/solo). These are the server's own field names (SignalR and minimal APIs both answer camelCase JSON); nothing here is a
// local invention. The browser owns no identity: the owner is always the guest cookie of the connection.
import { createMarrowTransport } from "@sands/contracts";

export const SOLO_HUB_PATH = "/hubs/ghostlap-solo";
export const ORIGINALS_ROUTE = "/api/ghostlap/solo/originals";
export const CHILDREN_ROUTE = "/api/ghostlap/solo/children";
/** `SoloOriginals.MaxListLimit`: the library list is capped here and has no paging contract. */
export const ORIGINALS_LIST_CAP = 100;
export const CHILDREN_PAGE = 10;

// ---- hub ---------------------------------------------------------------------------------------------------------------------------
export interface SoloRunStartedWire {
  operationKey: string; runId: string; seed: number; mascotId: number; rulesVersion: string; layoutVersion: string; tickRateHz: number; roundTicks: number;
}
export interface GhostEventWire { kind: string; slot: number; otherSlot: number; value: number; candleId: string }
/** Original Frame. The trailing projection fields remain optional for older hosts. */
export interface SoloFrameWire {
  tick: number; xFixed: number; yFixed: number; charge: number; overlitDeadlineTick: number; totalScore: number; final: boolean;
  distanceFixed?: number; overlit?: boolean; movementPoints?: number; pickupPoints?: number; penaltyPoints?: number; pickups?: number; bursts?: number;
  burstHits?: number; heldMoveX?: number; heldMoveY?: number; burstLatch?: boolean; collectedCount?: number; ackedInputSeq?: number;
  activeCandleIds?: string[] | null; events?: GhostEventWire[] | null;
  visibleCandles?: { id: string; x: number; y: number; phase: "active" | "telegraph" }[] | null;
  seq?: number; missed?: { tick: number; events: GhostEventWire[] }[] | null; missedTruncated?: boolean;
}
export interface SoloInputItemWire { seq: number; clientTick: number; moveX: number; moveY: number; requestBurst: boolean }
export interface SoloInputRejectedWire { seq: number; code: string; burstRequested: boolean; class: string; nextServerTick: number }
export interface SoloRunEndedWire { reason: string; code: string | null }

export interface SoloOriginalWire {
  id: string; operationKey: string; seed: number; mascotId: number; rulesVersion: string; layoutVersion: string; domainId: string; reducerVersion: string;
  inputSchemaVersion: string; stateSchemaVersion: string; contextSchemaVersion: string; finalTick: number; finalStateHash: string; lastRecordHash: string;
  /** Present only on a verified read of ONE original (the list never carries it). */
  score: number | null;
}
export interface SoloSaveWire { status: string; original: SoloOriginalWire | null; code: string | null }
export interface SoloPinnedWire { domainId: string; reducerVersion: string; inputSchemaVersion: string; stateSchemaVersion: string; contextSchemaVersion: string; rulesVersion: string; layoutVersion: string }
export interface SoloChildWire {
  id: string; sourceId: string; kind: string; operationKey: string; forkTick: number; finalTick: number; finalStateHash: string; lastRecordHash: string;
  score: number | null; verified: boolean; status: string; seed: number; mascotId: number; versions: SoloPinnedWire;
}
export interface SoloChildPageWire { items: SoloChildWire[]; nextAfter: string | null }

// ---- failures ----------------------------------------------------------------------------------------------------------------------
/** A hub method answered `{ ok: false }`. `code` is the server's own SoloFailure / ChallengeFailure string. */
export class SoloHubError extends Error {
  readonly method: string;
  readonly code: string;
  constructor(method: string, code: string) { super(`${method}: ${code}`); this.name = "SoloHubError"; this.method = method; this.code = code; }
}

export type HttpFailureKind = "offline" | "unauthorized" | "not_found" | "busy" | "unavailable" | "conflict" | "bad_request" | "malformed" | "unreachable";
/** An owner-library read failed. `kind` drives the wording; `status` and `code` are the server's own. */
export class SoloHttpError extends Error {
  readonly kind: HttpFailureKind;
  readonly status: number;
  readonly code: string | null;
  constructor(kind: HttpFailureKind, status: number, code: string | null, message: string) { super(message); this.name = "SoloHttpError"; this.kind = kind; this.status = status; this.code = code; }
}

const HUB_TEXT: Readonly<Record<string, string>> = {
  forbidden: "The game server refused this session. Reload the page to start a fresh one.",
  unavailable: "The game server is unavailable right now.",
  invalid_request: "That request wasn't valid.",
  already_saved: "This run is already saved.",
  interrupted: "That run was interrupted before it finished, so it was never saved.",
  conflict: "That run key was already used for a different run.",
  run_active: "You already have a run in progress, possibly in another tab. Finish or close it first.",
  server_busy: "The server is busy with other runs. Try again in a moment.",
  no_run: "There is no live run on this connection.",
  stream_already_open: "This connection is already sending input.",
  not_found: "The server has no finished run for that key.",
  not_complete: "The server has no finished run for that key.",
  verification_failed: "The server could not verify this recording.",
  corrupt_history: "The stored recording failed its integrity check.",
  version_unsupported: "This recording was made under rules this server no longer supports.",
};

export function hubFailureText(error: unknown): string {
  if (error instanceof SoloHubError) return HUB_TEXT[error.code] ?? `The server answered "${error.code}".`;
  if (typeof error === "string") return error;
  if (error instanceof Error) return error.message;
  return String(error);
}

export function httpFailureText(error: unknown): string {
  if (!(error instanceof SoloHttpError)) return error instanceof Error ? error.message : String(error);
  switch (error.kind) {
    case "offline": return "The server can't be reached.";
    case "unauthorized": return "Your guest session isn't valid. Reload the page to start a fresh one.";
    case "not_found": return "That history isn't available to you. It may not be yours, or it never finished.";
    case "busy": return "The server is verifying other histories. Try again in a moment.";
    case "unavailable": return "The history service is unavailable right now.";
    case "conflict": return "The server couldn't verify this history (its stored record failed a check or uses unsupported rules).";
    case "bad_request": return "The server refused that request.";
    case "unreachable": return "The history service didn't answer as a game server would. Solo may not be enabled on this server, or its route isn't reachable from here.";
    default: return "The history service sent something this page doesn't understand.";
  }
}

// ---- shape checks ------------------------------------------------------------------------------------------------------------------
const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const isInt = (v: unknown): v is number => typeof v === "number" && Number.isSafeInteger(v);
// The C# contract sends an Int64 seed. JavaScript may round it, and Solo uses it only as server metadata.
const isSeed = (v: unknown): v is number => typeof v === "number" && Number.isInteger(v) && Math.abs(v) <= 2 ** 63;
const isStr = (v: unknown): v is string => typeof v === "string" && v.length > 0;

export function isOriginal(v: unknown): v is SoloOriginalWire {
  return isRecord(v) && isStr(v.id) && isStr(v.operationKey) && isSeed(v.seed) && isInt(v.mascotId) && v.mascotId >= 0 && v.mascotId <= 3 && isStr(v.rulesVersion) &&
    isStr(v.layoutVersion) && isStr(v.domainId) && isStr(v.reducerVersion) && isInt(v.finalTick) && isStr(v.finalStateHash) && isStr(v.lastRecordHash) &&
    (v.score === null || v.score === undefined || isInt(v.score));
}
export function normalizeOriginal(v: SoloOriginalWire): SoloOriginalWire { return { ...v, score: v.score ?? null }; }

export function isChild(v: unknown): v is SoloChildWire {
  return isRecord(v) && isStr(v.id) && isStr(v.sourceId) && (v.kind === "Challenge" || v.kind === "Fork") && isInt(v.forkTick) && isInt(v.finalTick) &&
    isStr(v.finalStateHash) && typeof v.verified === "boolean" && (v.score === null || v.score === undefined || isInt(v.score)) && isInt(v.mascotId) && isRecord(v.versions);
}
export function isSave(v: unknown): v is SoloSaveWire {
  return isRecord(v) && isStr(v.status) && (v.original === null || v.original === undefined || isOriginal(v.original));
}
export function isRunStarted(v: unknown): v is SoloRunStartedWire {
  return isRecord(v) && isStr(v.runId) && isStr(v.operationKey) && isInt(v.mascotId) && isStr(v.rulesVersion) && isStr(v.layoutVersion) && isInt(v.tickRateHz) && isInt(v.roundTicks);
}

// ---- guest session + HTTP reads ----------------------------------------------------------------------------------------------------
/** Same bootstrap every hub screen uses: contract handshake, then the guest cookie (GET /auth/guest/me, else POST /auth/guest/start). */
export async function ensureGuestSession(signal?: AbortSignal): Promise<void> {
  const http = createMarrowTransport({ apiBaseUrl: "", realtimeBaseUrl: "" });
  const check = await http.negotiate(signal);
  if (!check.ok || !check.value.compatible) throw new Error("The API contract is unavailable or incompatible.");
  const me = await http.api.get("/auth/guest/me");
  if (me.ok) return;
  const guest = await http.api.post("/auth/guest/start");
  if (!guest.ok) throw new Error("Guest authentication failed.");
}

function classify(status: number, code: string | null): HttpFailureKind {
  if (status === 401 || status === 403) return "unauthorized";
  if (status === 404) return "not_found";
  if (status === 429) return "busy";
  if (status === 409) return "conflict";
  if (status === 400) return "bad_request";
  if (status === 503 || status >= 500) return "unavailable";
  return code ? "bad_request" : "malformed";
}

/** One owner-scoped GET. A host that does not serve the route (no proxy, solo disabled) often answers an HTML page: that is `unreachable`, not malformed data. */
export async function getJson(path: string, signal?: AbortSignal, fetchImpl: typeof fetch = fetch): Promise<unknown> {
  let response: Response;
  try {
    response = await fetchImpl(path, { method: "GET", headers: { Accept: "application/json" }, credentials: "include", cache: "no-store", ...(signal ? { signal } : {}) });
  } catch (error) {
    if (signal?.aborted) throw error;
    throw new SoloHttpError("offline", 0, null, "offline");
  }
  const text = await response.text();
  const typed = (response.headers.get("content-type") ?? "").includes("json");
  let body: unknown = null;
  if (text && typed) { try { body = JSON.parse(text); } catch { body = null; } }
  if (!response.ok) {
    const code = isRecord(body) && typeof body.code === "string" ? body.code : null;
    if (!typed && response.status !== 401 && response.status !== 404 && response.status !== 429 && response.status < 500) throw new SoloHttpError("unreachable", response.status, null, "unreachable");
    if (!typed && response.status === 404) throw new SoloHttpError("unreachable", response.status, null, "unreachable");
    throw new SoloHttpError(classify(response.status, code), response.status, code, `HTTP ${response.status}`);
  }
  if (!typed) throw new SoloHttpError("unreachable", response.status, null, "unreachable");
  if (body === null) throw new SoloHttpError("malformed", response.status, null, "malformed");
  return body;
}

export async function listOriginals(signal?: AbortSignal, fetchImpl?: typeof fetch): Promise<SoloOriginalWire[]> {
  const body = await getJson(ORIGINALS_ROUTE, signal, fetchImpl);
  if (!Array.isArray(body) || !body.every(isOriginal)) throw new SoloHttpError("malformed", 200, null, "malformed list");
  return body.map(normalizeOriginal);
}

/** One verified original with its score. The server replays the stored history for this read, so it is rationed (four slots, 429 when busy). */
export async function getOriginal(id: string, signal?: AbortSignal, fetchImpl?: typeof fetch): Promise<SoloOriginalWire> {
  const body = await getJson(`${ORIGINALS_ROUTE}/${encodeURIComponent(id)}`, signal, fetchImpl);
  if (!isOriginal(body)) throw new SoloHttpError("malformed", 200, null, "malformed original");
  return normalizeOriginal(body);
}

export async function listChildren(after: string | null, signal?: AbortSignal, fetchImpl?: typeof fetch): Promise<SoloChildPageWire> {
  const query = new URLSearchParams({ limit: String(CHILDREN_PAGE) });
  if (after) query.set("after", after);
  const body = await getJson(`${CHILDREN_ROUTE}?${query.toString()}`, signal, fetchImpl);
  if (!isRecord(body) || !Array.isArray(body.items) || !body.items.every(isChild) || !(body.nextAfter === null || body.nextAfter === undefined || typeof body.nextAfter === "string")) {
    throw new SoloHttpError("malformed", 200, null, "malformed children");
  }
  return { items: body.items as SoloChildWire[], nextAfter: (body.nextAfter as string | null | undefined) ?? null };
}
