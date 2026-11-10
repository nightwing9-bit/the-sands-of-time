// HTTP wire of the Ghost Lap greybox (spec 06, task 6.5) to the /ghost-lap routes served by the TEST-ONLY host in tests/E2E/backend.
// Hosts.Api serves no /ghost-lap route yet and the OpenAPI document lists none; the production realtime session (Spec 10) and the
// catalog (Spec 11) replace this module only. Failures are thrown as `{ failure: TransportFailure }`, like the Timeline wire, and
// every response is shape-checked before a component sees it, so a malformed answer is a typed failure, never a half-drawn arena.
import { currentCsrfToken, parsePosition, type TimelinePosition, type TransportFailure } from "@sands/contracts";
import { call, csrfHeader, fail } from "../ghostLapTimelineWire.ts";
import type { Arena, GhostView, InputAnswer, Intent, LiveFrame, OriginalDescriptor, WorldView } from "./types.ts";

export interface GreyboxWire {
  arena(signal?: AbortSignal): Promise<Arena>;
  startOriginal(seed: number, mascotId: number, operationKey: string): Promise<LiveFrame>;
  startLoop(sessionId: string): Promise<void>;
  frame(sessionId: string, signal?: AbortSignal): Promise<LiveFrame>;
  input(sessionId: string, sequence: number, clientTick: number, intent: Intent): Promise<InputAnswer>;
  neutral(sessionId: string): Promise<InputAnswer>;
  original(id: string, signal?: AbortSignal): Promise<OriginalDescriptor>;
  restore(id: string, position: TimelinePosition, signal?: AbortSignal): Promise<{ position: TimelinePosition; state: WorldView }>;
  branch(id: string, position: TimelinePosition, operationKey: string): Promise<{ branchId: string }>;
}

const malformed = (what: string): never => fail({ kind: "malformed", message: `The ${what} was not recognised.` } satisfies TransportFailure);
const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const isInt = (v: unknown): v is number => typeof v === "number" && Number.isSafeInteger(v);
const isNum = (v: unknown): v is number => typeof v === "number" && Number.isFinite(v);
const isStr = (v: unknown): v is string => typeof v === "string" && v.length > 0;

function checkWorld(v: unknown): v is WorldView {
  const candle = (c: unknown) => isRecord(c) && isStr(c.id) && isNum(c.x) && isNum(c.y) && isInt(c.activateTick) && isInt(c.expireTick);
  return (
    isRecord(v) && isInt(v.tick) && v.tick >= 0 && typeof v.isFinal === "boolean" && isInt(v.collectedCount) &&
    Array.isArray(v.players) && v.players.length > 0 &&
    v.players.every((p) => isRecord(p) && isNum(p.x) && isNum(p.y) && isInt(p.charge) && p.charge >= 0 && p.charge <= 4 && isInt(p.totalScore) &&
      isInt(p.movementPoints) && isInt(p.pickupPoints) && isInt(p.penaltyPoints) && isInt(p.pickups) && isInt(p.bursts) && isInt(p.lastSequence) &&
      typeof p.overlit === "boolean") &&
    Array.isArray(v.activeCandles) && v.activeCandles.every(candle) && Array.isArray(v.telegraphCandles) && v.telegraphCandles.every(candle) &&
    Array.isArray(v.events)
  );
}

function checkGhost(v: unknown): v is GhostView {
  return isRecord(v) && isInt(v.tick) && isNum(v.x) && isNum(v.y) && isInt(v.charge) && isInt(v.totalScore) && isInt(v.pickups);
}

function checkFrame(v: unknown): v is LiveFrame {
  return (
    isRecord(v) && isStr(v.sessionId) && (v.kind === "original" || v.kind === "branch") && isStr(v.status) && isInt(v.tickRate) &&
    checkWorld(v.world) && (v.ghost === null || checkGhost(v.ghost)) &&
    (v.aligned === null || (isRecord(v.aligned) && isInt(v.aligned.tick) && isInt(v.aligned.ghostScore) && isInt(v.aligned.currentScore) &&
      isInt(v.aligned.delta) && typeof v.aligned.isFinal === "boolean"))
  );
}

function checkArena(v: unknown): v is Arena {
  return isRecord(v) && isInt(v.width) && isInt(v.height) && isInt(v.tickRate) && isInt(v.roundTicks) && isInt(v.colliderHalf) &&
    isInt(v.candleHalf) && isInt(v.burstRadius) && Array.isArray(v.obstacles) &&
    v.obstacles.every((o) => isRecord(o) && isInt(o.x0) && isInt(o.y0) && isInt(o.x1) && isInt(o.y1));
}

function checkOriginal(v: unknown): v is OriginalDescriptor {
  return (
    isRecord(v) && isStr(v.timelineId) && isStr(v.sourceId) && isRecord(v.bounds) && parsePosition(v.bounds.start) !== null &&
    parsePosition(v.bounds.end) !== null && typeof v.readOnly === "boolean" && Array.isArray(v.markers) &&
    v.markers.every((m) => isRecord(m) && parsePosition(m.position) !== null && isStr(m.label)) && isInt(v.finalScore) &&
    isStr(v.finalStateHash) && checkArena(v.arena)
  );
}

function checkAnswer(v: unknown): v is InputAnswer {
  return isRecord(v) && typeof v.accepted === "boolean" && isInt(v.nextTick) && isInt(v.lastSequence);
}

export function createGreyboxHttpWire(
  doFetch: typeof fetch = (...args) => fetch(...args),
  getCsrfToken: () => string | null = currentCsrfToken,
): GreyboxWire {
  const json = { Accept: "application/json" };
  const post = (url: string, body: unknown, extra: Record<string, string> = {}) =>
    call(doFetch, url, {
      method: "POST",
      headers: { ...json, "Content-Type": "application/json", ...extra, ...csrfHeader(getCsrfToken()) },
      body: JSON.stringify(body ?? {}),
    });
  const id = encodeURIComponent;
  return {
    async arena(signal) {
      const body = await call(doFetch, "/ghost-lap/arena", { method: "GET", headers: json, signal });
      return checkArena(body) ? body : malformed("arena");
    },
    async startOriginal(seed, mascotId, operationKey) {
      const body = await post("/ghost-lap/solo/sessions", { seed, mascotId }, { "Idempotency-Key": operationKey });
      return checkFrame(body) ? body : malformed("solo session");
    },
    async startLoop(sessionId) {
      await post(`/ghost-lap/sessions/${id(sessionId)}/start`, {});
    },
    async frame(sessionId, signal) {
      const body = await call(doFetch, `/ghost-lap/sessions/${id(sessionId)}/frame`, { method: "GET", headers: json, signal });
      return checkFrame(body) ? body : malformed("frame");
    },
    async input(sessionId, sequence, clientTick, intent) {
      const body = await post(`/ghost-lap/sessions/${id(sessionId)}/inputs`, { sequence, clientTick, ...intent });
      return checkAnswer(body) ? body : malformed("input answer");
    },
    async neutral(sessionId) {
      const body = await post(`/ghost-lap/sessions/${id(sessionId)}/neutral`, {});
      return checkAnswer(body) ? body : malformed("input answer");
    },
    async original(originalId, signal) {
      const body = await call(doFetch, `/ghost-lap/originals/${id(originalId)}`, { method: "GET", headers: json, signal });
      return checkOriginal(body) ? body : malformed("original");
    },
    async restore(originalId, position, signal) {
      const body = (await call(doFetch, `/ghost-lap/originals/${id(originalId)}/restore?position=${id(position)}`, { method: "GET", headers: json, signal })) as
        { position?: unknown; state?: unknown } | undefined;
      if (typeof body?.position !== "string" || parsePosition(body.position) === null || !checkWorld(body.state)) return malformed("restored state");
      return { position: body.position, state: body.state };
    },
    async branch(originalId, position, operationKey) {
      const body = (await post(`/ghost-lap/originals/${id(originalId)}/branches`, { position, operationKey }, { "Idempotency-Key": operationKey })) as
        { branchId?: unknown } | undefined;
      return isStr(body?.branchId) ? { branchId: body.branchId } : malformed("branch");
    },
  };
}
