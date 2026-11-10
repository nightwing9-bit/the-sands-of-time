// HTTP wire of the Revenant slice (spec 05, task 5.4) to the slice routes served by the TEST-ONLY host in tests/E2E/backend.
// Hosts.Api serves no /revenant route yet and the OpenAPI document lists none; the production Revenant API replaces this module only.
// Failures are thrown as `{ failure: TransportFailure }`, like the Timeline wire. Responses are shape-checked before any component
// sees them, so a malformed answer is a typed failure and never a half-rendered screen.
import { currentCsrfToken, parsePosition, type TimelinePosition, type TransportFailure } from "@sands/contracts";
import { call, csrfHeader, fail } from "../revenantTimelineWire.ts";
import type { BranchOptions, RunDescriptor, SliceComparison, SliceState } from "./types.ts";

export interface SliceWire {
  startRun(seed: number, operationKey: string, signal?: AbortSignal): Promise<RunDescriptor>;
  restore(timelineId: string, position: TimelinePosition, signal?: AbortSignal): Promise<{ position: TimelinePosition; state: SliceState }>;
  createBranch(timelineId: string, position: TimelinePosition, operationKey: string, options: BranchOptions): Promise<{ branchId: string }>;
  comparison(branchId: string, signal?: AbortSignal): Promise<SliceComparison>;
}

const malformed = (what: string): never => fail({ kind: "malformed", message: `The ${what} was not recognised.` } satisfies TransportFailure);
const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const isInt = (v: unknown): v is number => typeof v === "number" && Number.isSafeInteger(v);
const isStr = (v: unknown): v is string => typeof v === "string" && v.length > 0;
const isArr = (v: unknown): v is unknown[] => Array.isArray(v);

function checkLabelling(v: unknown): boolean {
  return isRecord(v) && typeof v.synthetic === "boolean" && typeof v.projectionOnly === "boolean" && isStr(v.ledgerPosting) && isStr(v.note);
}
function checkState(v: unknown): v is SliceState {
  return (
    isRecord(v) && isStr(v.businessTime) && isStr(v.stateHash) && isStr(v.currency) && isInt(v.totalPostedMinor) && isInt(v.totalReservedMinor) &&
    isArr(v.banks) && v.banks.every((b) => isRecord(b) && isStr(b.bankId) && isInt(b.postedMinor) && isInt(b.reservedMinor) && isInt(b.availableMinor)) &&
    isArr(v.payments) && v.payments.every((p) => isRecord(p) && parsePosition(p.position) !== null && isStr(p.status) && isStr(p.reason) && isStr(p.policyId)) &&
    isRecord(v.counters) && isInt(v.counters.fraudPendingHold) && checkLabelling(v.labelling)
  );
}
function checkDescriptor(v: unknown): v is RunDescriptor {
  return (
    isRecord(v) && isStr(v.timelineId) && isStr(v.runId) && isStr(v.sourceId) && isRecord(v.bounds) &&
    parsePosition(v.bounds.start) !== null && parsePosition(v.bounds.end) !== null && typeof v.readOnly === "boolean" &&
    isArr(v.markers) && v.markers.every((m) => isRecord(m) && parsePosition(m.position) !== null && isStr(m.label)) &&
    isStr(v.sourcePolicyId) && isRecord(v.scenario) && checkLabelling(v.labelling) &&
    isArr(v.banks) && v.banks.every((b) => isRecord(b) && isStr(b.bankId) && isInt(b.openingMinor)) &&
    isArr(v.events) && v.events.every((e) => isRecord(e) && parsePosition(e.position) !== null && isStr(e.intentId) && isInt(e.amountMinor) && isStr(e.syntheticLabel)) &&
    isArr(v.policies) && v.policies.every((p) => isRecord(p) && isStr(p.policyId) && isInt(p.velocityHoldThreshold))
  );
}
function checkComparison(v: unknown): v is SliceComparison {
  return (
    isRecord(v) && isStr(v.branchId) && isStr(v.sourceId) && parsePosition(v.forkPosition) !== null && parsePosition(v.finalPosition) !== null &&
    isStr(v.sourcePolicyId) && isStr(v.sourceFinalStateHash) && isStr(v.branchFinalStateHash) &&
    isArr(v.aligned) && v.aligned.length > 0 &&
    v.aligned.every(
      (a) =>
        isRecord(a) && parsePosition(a.position) !== null && checkState(a.original) && checkState(a.alternate) &&
        isRecord(a.metrics) && isInt(a.metrics.fraudPreventedCount) && isInt(a.metrics.fraudDeferredAtHorizonCount) && isInt(a.metrics.netEffectMinor),
    ) &&
    isArr(v.differences) && isRecord(v.sourceCheck) && typeof v.sourceCheck.unchanged === "boolean" && checkLabelling(v.labelling)
  );
}

export function createSliceHttpWire(
  doFetch: typeof fetch = (...args) => fetch(...args),
  getCsrfToken: () => string | null = currentCsrfToken,
): SliceWire {
  const json = { Accept: "application/json" };
  const post = (url: string, key: string, body: unknown) =>
    call(doFetch, url, {
      method: "POST",
      headers: { ...json, "Content-Type": "application/json", "Idempotency-Key": key, ...csrfHeader(getCsrfToken()) },
      body: JSON.stringify(body),
    });
  return {
    async startRun(seed, operationKey) {
      const body = await post("/revenant/runs", operationKey, { seed });
      return checkDescriptor(body) ? body : malformed("run description");
    },
    async restore(timelineId, position, signal) {
      const body = await call(doFetch, `/revenant/runs/${encodeURIComponent(timelineId)}/restore?position=${encodeURIComponent(position)}`, { method: "GET", headers: json, signal });
      if (!isRecord(body) || typeof body.position !== "string" || !checkState(body.state)) return malformed("preview");
      return { position: body.position, state: body.state };
    },
    async createBranch(timelineId, position, operationKey, options) {
      const body = await post(`/revenant/runs/${encodeURIComponent(timelineId)}/branches`, operationKey, { position, operationKey, options });
      if (!isRecord(body) || !isStr(body.branchId)) return malformed("branch confirmation");
      return { branchId: body.branchId };
    },
    async comparison(branchId, signal) {
      const body = await call(doFetch, `/revenant/branches/${encodeURIComponent(branchId)}/comparison`, { method: "GET", headers: json, signal });
      return checkComparison(body) ? body : malformed("comparison");
    },
  };
}
